# UI Look & Feel — Shared Product Style Guide

> One visual language for every product we ship (Data Noodler, Bridge Warden, and the rest).
> Source of truth: the reference screenshots in `docs/design/reference/`. Measurements below were taken from those screenshots at 100% scaling (window about 1550×970). Where a value is estimated, it is rounded to the 2/4 px grid.

---

## 1. Design principles

1. **Operator console, not a website.** Dense, dark, calm. Information first. No hero images, no gradients, no shadows beyond modal elevation, no rounded "bubbly" cards.
2. **Two window archetypes.** Every product has a **Control window** (red logo, engine-room view: status, workers, big action button, live log) and a **Main app window** (blue logo, sidebar + table + detail drawer). Tool windows (Studio, Benchmark, Transform) are a third, chrome-light variant.
3. **Colour means something.** Blue = navigate/primary, green = healthy/go, orange = special/dev/caution, red = stop/destructive/critical, yellow = in progress/pending, purple = classification/policy. Never use a colour decoratively.
4. **Text always backs colour.** Every coloured state also has a word (ONLINE, TESTING, 5%).
5. **Flat and squared.** Small radii (3–6 px), 1 px borders, solid fills.
6. **Monospace for machine data.** Timestamps, IDs, UUIDs, slugs, paths, code, log lines and metric values in tiles use the mono font. Everything human-facing uses the UI font.

---

## 2. Typography

| Role | Font | Notes |
|---|---|---|
| UI | **Exo 2** (fallback: `"Segoe UI", sans-serif`) | Geometric, slightly squared sans. Matches titles, buttons, tables. |
| Mono | **Syne Mono** (fallback: `"JetBrains Mono", Consolas, monospace`) | The Data Noodler server log shows `/assets/fonts/SyneMono-Regular.ttf` being loaded, so this is the mono face already in use. Narrow, with a distinctive `r`. |

Bundle both locally (`/assets/fonts/` or `@fontsource`). Never load from Google at runtime.

### Type scale

| Use | Size | Weight | Colour | Case |
|---|---|---|---|---|
| Window header title ("Data Noodler Server") | 20 px | 700 | `--text` | Title |
| Header subtitle (path / version) | 11 px | 400 | `--text-muted` | as-is |
| Sidebar "Menu" title | 20 px | 700 | `--text` | Title |
| Drawer title | 22 px | 700 | `--text` | Title |
| Modal title | 16 px | 700 | `--text` | Title |
| Section label, coloured (SERVER ACTIONS, DEV ACTIONS) | 12–13 px | 700 | blue / orange | UPPER, tracking 0.08em |
| Section label, neutral (INPUT TABLES, ACTIVITY, APPEARANCE) | 12 px | 700 | `--text` or `--text-muted` | UPPER, tracking 0.08em |
| Table header | 11 px | 600 | `--text-muted` | UPPER, tracking 0.04em |
| Table row primary cell | 13–14 px | 700 | `--text` | as-is |
| Table row secondary cell | 12–13 px | 400 | `--text-muted` | as-is |
| Body / description | 13–14 px | 400 | `--text` | sentence |
| Small label above a value ("Current User", "Uptime") | 12 px | 400 | `--text-muted` | sentence |
| Big value under a label ("christopher_staggs", "0:00:14") | 18–20 px | 700 | `--text` | as-is |
| Header KPI value ("23.59 GB") | 18 px | 700 | `--text` | as-is |
| Status word ("ONLINE") | 18–20 px | 700 | `--green` | UPPER |
| Button | 13–14 px | 700 | white | Title Case |
| Button sub-caption ("(Hold Shift For Restart)") | 10–11 px | 400 | white @ 85% | as-is |
| Badge | 10–11 px | 700 | badge fg | UPPER, tracking 0.04em |
| Log line | 11–12 px | 400 mono | per log colour | as-is |
| ID / UUID / slug | 10–11 px | 400 mono | `--text-muted` | as-is |
| Stat tile caps label (READ, ENGINE MEMORY) | 11 px | 600 | `--text-muted` | UPPER |
| Stat tile big value, mono ("239.0 MB") | 22–24 px | 400 mono | `--text` or semantic | as-is |

---

## 3. Colour tokens

Drop this file in every product as `styles/tokens.css`. Values are sampled from the screenshots.

```css
:root {
  /* ---- surfaces (darkest → lightest) ---- */
  --bg-titlebar:    #000000;  /* OS title bar */
  --bg-app:         #1b1b1b;  /* main content area */
  --bg-panel:       #1e1e1e;  /* sidebar, left control panel, drawer */
  --bg-inset:       #161616;  /* log console, code editor, input fields */
  --bg-tile:        #232323;  /* stat tiles, toolbar icon tiles */
  --bg-row:         #2c2c2c;  /* table row cards */
  --bg-row-hover:   #343434;
  --bg-modal:       #232323;
  --scrim:          rgba(0,0,0,.55);  /* behind modals */

  --border:         #2e2e2e;  /* hairline dividers, header bottom line */
  --border-strong:  #3d3d3d;  /* tile and input borders */
  --grid-line:      rgba(255,255,255,.035); /* canvas grid in Studio windows */

  /* ---- text ---- */
  --text:           #f0f0f0;
  --text-muted:     #9a9a9a;
  --text-dim:       #6a6a6a;  /* disabled, placeholders */

  /* ---- actions ---- */
  --blue:           #3b82f6;  /* primary button, active nav pill, tabs, links, focus */
  --blue-hover:     #2f6fd6;
  --green:          #3fae6a;  /* big start/stop button, Save, Run, Execute, ONLINE */
  --green-hover:    #369a5d;
  --orange:         #e8892b;  /* dev actions, special primary (Data Studio) */
  --orange-hover:   #d07a24;
  --red:            #e5484d;  /* Delete, Delete All, critical */
  --red-hover:      #cc3e43;
  --grey-btn:       #3a3a3a;  /* secondary buttons (Trash, Edit Details, Close, Cancel) */
  --grey-btn-hover: #444444;

  /* ---- logo tiles ---- */
  --logo-control:   #e5484d;  /* server / control window */
  --logo-app:       #3b82f6;  /* main app window */

  /* ---- badges (fg = text + border; bg = fg @ 15%) ---- */
  --badge-green:    #4cc38a;  /* UNCLASSIFIED, ACTIVE, APPROVED */
  --badge-blue:     #5aa2ff;  /* TESTING, info, RECOMMEND */
  --badge-purple:   #b58cf0;  /* CUI, policy / regulatory */
  --badge-yellow:   #e2b33c;  /* DEVELOPMENT, PENDING, ESCALATED */
  --badge-red:      #ff6b6b;  /* REJECTED, QUARANTINED, CRITICAL */
  --badge-grey:     #9a9a9a;  /* IDLE, PAUSED, EXPIRED */

  /* Notification banners (7.17) */
  --notify-system:  #ee7ab6;  /* pink system notice, sampled from 230759.png */
  --notify-text-on-yellow: #1b1b1b;

  /* ---- percent / score text ---- */
  --pct-low:        #ff6b6b;  /* e.g. 5% */
  --pct-mid:        #f0a24a;  /* e.g. 25% */
  --pct-high:       #4cc38a;

  /* ---- log console ---- */
  --log-time:       #b8b8b8;  /* timestamp column */
  --log-system:     #ff6fa3;  /* pink: server / system lifecycle */
  --log-engine:     #4dd0e1;  /* cyan: engine / agent messages */
  --log-ok:         #4cc38a;  /* green: success, "Previewed …" */
  --log-warn:       #f0a24a;  /* orange: warnings ("will not fit in memory") */
  --log-error:      #ff6b6b;
  --log-get:        #5aa2ff;  /* HTTP method colours */
  --log-post:       #8f7cf0;
  --log-highlight-bg: #3fae6a; /* full-width milestone row ("SERVER STARTED") */
  --log-highlight-fg: #0b1a10;

  /* ---- worker / agent state dots ---- */
  --state-idle:     #3fae6a;  /* round dots in tool windows: green = idle/ready */
  --state-read:     #3b82f6;
  --state-write:    #e5484d;
  --state-off:      #4a4a4a;  /* square tiles in control window: grey = idle */

  /* ---- geometry ---- */
  --radius-xs: 3px;   /* worker tiles, badges */
  --radius-sm: 4px;   /* buttons, inputs */
  --radius:    6px;   /* row cards, stat tiles, logo tile */
  --radius-lg: 8px;   /* modals, drawer panels */

  --font-ui:   "Exo 2", "Segoe UI", sans-serif;
  --font-mono: "Syne Mono", "JetBrains Mono", Consolas, monospace;
}
```

### Light theme

The Settings modal offers **Appearance (all windows): Dark / Light**, and the choice applies to every window at once. Dark is the default and the one to design against. Light is a token swap only (`:root[data-theme="light"]` overriding surfaces and text). Components must never hard-code colours, so the swap works for free. For hackathon builds, ship dark only but keep everything on tokens.

---

## 4. Spacing & layout grid

- Base unit **4 px**. Common gaps: 4, 6, 8, 12, 16, 20, 24.
- Outer page padding: **20 px** (control panel), **24 px** (main content), **24 px** (drawer).
- Gap between table row cards: **6 px**.
- Gap between 2-column buttons: **6 px** horizontal, **6 px** vertical.
- Section spacing (end of one block to next section label): **28–32 px**.

### Window chrome

| Element | Size |
|---|---|
| OS title bar | 30 px, `--bg-titlebar`, 16 px logo glyph + window title in 13 px white. Format: `<Tool> · <Context>` e.g. "Transform · Demo Project For Development". |
| App header | ~72 px tall, `--bg-app`, bottom 1 px `--border`. |
| Logo tile | 32–40 px square, `--radius`, white 2-letter initials 14 px/700. Red in Control windows, blue in the main app. |
| Header title block | 12 px left of logo. In the main app a 1 px vertical divider sits between logo and title. |
| Header KPIs | right-aligned, 28 px gap between stats, label on top, value below, both right-aligned. |

---

## 5. Window archetypes

### 5.1 Control window ("Server" pattern)

```
┌ title bar ───────────────────────────────────────────────────────────────┐
│ [RED LOGO] Product Server                        KPI   KPI   KPI   KPI    │
│            D:\path\or\context                    val   val   val   val    │
├──────────────────────────┬───────────────────────────────────────────────┤
│ LEFT PANEL 365 px        │ LOG CONSOLE (fills rest)                      │
│ label / big value pairs  │ TIMESTAMP   COLS...                           │
│ 2-col status words       │ mono lines, coloured by source                │
│ state legend + tiles     │ ████ highlighted milestone row ████           │
│ [ BIG HOLD BUTTON 54px ] │                                               │
│ BLUE SECTION LABEL       │                                               │
│ [btn][btn] 2-col grid    │                                               │
│ ORANGE SECTION LABEL     │                                               │
│ [btn][btn]               │                                               │
└──────────────────────────┴───────────────────────────────────────────────┘
```

- Left panel: `--bg-app` with a 1 px right border. Label/value pairs go in a 2-column grid (column 2 starts about 165 px in).
- **Big action button:** full panel width, 54 px tall, green when the system is running ("Stop Server"), with a sub-caption describing a modifier ("Hold Shift For Restart"). It is colour-coded by *current state*, not by action.
- **Action grids:** buttons ~157×25 px, 2 columns, solid fill, centred bold label. An odd last button sits in the left column; it does not stretch.
- Log console: no box. Column headers sit directly on the background in muted mono caps. A wrapped line indents under the message column, not under the timestamp.
- Modals open centred over this window with a scrim that dims everything, including the log (see §7.10).

### 5.2 Main app window ("Project manager" pattern)

```
┌ title bar ──────────────────────────────────────────────────────────────┐
│ [BLUE LOGO] │ Product (Workspace)                                       │
│             │ v.26_1014_0724                                            │
├─────────────┬───────────────────────────────────────────────────────────┤
│ Menu      < │ [Primary][Secondary]                                      │
│ ▣ Item  ◀━━ │ COL  COL  COL  COL ...              (muted caps header)   │
│ ▣ Item      │ ┌ row card ────────────────────────────────────────────┐  │
│ ▣ Item      │ └──────────────────────────────────────────────────────┘  │
│             │ ┌ row card ────────────────────────────────────────────┐  │
│ Group  ˅  + │ └──────────────────────────────────────────────────────┘  │
│   sub item  │                                                           │
│ Help   ˅    │                                                           │
│ ▣ Item      │                                                           │
└─────────────┴───────────────────────────────────────────────────────────┘
```

- Sidebar 248 px wide, 1 px right border, padding 8 px.
- When a row is selected, the table **compresses to its key columns** (name, classification, phase, status) and a detail drawer opens on the right, about 44% of the window width (≥ 640 px). See §7.8.

### 5.3 Tool / Studio window (canvas pattern)

Used for Data Studio, Dashboard Studio, Transform and Benchmark.

- No app header. The OS title bar carries the context ("Pipeline 1 · Data Studio").
- **Canvas** background: `--bg-app` with a faint 1 px grid (about 96 px major cells, `--grid-line`).
- **Icon toolbar** centred at the top: tiles about 64×58 px, `--bg-tile`, 1 px `--border-strong`, `--radius`. Each has a coloured icon (24 px) over a 12 px label, and the label truncates with an ellipsis ("Transfo…"). Selected tile = lighter border + white label. Disabled = 40% opacity.
- **Floating metric chips** top-right: small `--bg-tile` boxes with a muted label ("Memory", "Workers") and a bold value.
- **Primary run action** centred at the bottom: green, with a play icon ("▶ Execute").
- Split-pane variant (Transform): a code editor on the left in `--bg-inset` with a line-number gutter, and a right panel 420 px wide with neutral caps section labels and list rows.

---

## 6. Iconography

- **Lucide** line icons, 1.75 stroke, 16 px in the sidebar and drawer key/value grids, 24 px in toolbar tiles.
- Sidebar icons are white. Drawer KV icons are `--text-muted`.
- Toolbar tile icons take a semantic colour: Input blue, Transform purple, SQL cyan, Function green, Join orange, Append yellow, Chart pink, Output orange-red, Publish/Archive white. Charts and other type pickers use grey icons.
- "Add" affordances are a thin `+` in `--green` before list values ("+ Staggs, Christopher C. CIV") or in muted grey at the right of a sidebar group header.

---

## 7. Components

### 7.1 Buttons

| Variant | Fill | Use |
|---|---|---|
| `primary` | `--blue` | New Project, Add, Open Module, Run (toolbar), New |
| `success` | `--green` | Save, Run (editor), Execute, big Stop/Start, Approve |
| `warn` | `--orange` | Dev actions, special primary in a drawer (Data Studio), Override |
| `danger` | `--red` | Delete, Delete All, Quarantine |
| `secondary` | `--grey-btn` | Trash, Edit Details, Archives, Cancel, Close, Syntax Library |
| `dropdown` | `--grey-btn` + `▾` | Benchmark filter chips ("Rows: 100k, 1M, 5M ▾") |
| `link` | none, `--blue` text 12 px | "Show More / Show Less" |
| `mini` | `--grey-btn`, 22 px tall, 12 px | "Edit" next to a section title |

- Height: **26 px** for toolbar and grid buttons, **32 px** for modal footer buttons, **54 px** for the big hold button.
- Padding 14 px horizontal; `--radius-sm`; label 13–14 px/700 white; no border; no shadow.
- Hover = `-hover` token. Active = 1 px inset darker. Focus = 2 px `--blue` outline offset 2. Disabled = `--grey-btn` with `--text-dim` text (see the "Stop" button in Benchmark).
- **Modal footer pattern:** either one full-width secondary "Close", or a 50/50 split with the positive action on the left (green "Save" | grey "Cancel").
- **Button row in a drawer:** left-aligned, 6 px gap, order = special (orange) → secondaries (grey) → destructive (red) last.
- **Hold button:** a progress fill sweeps left→right while held and fires at 100%. The sub-caption tells the user how ("Hold 2s to engage").

### 7.2 Badges

- Height 20 px, padding 0 8 px, `--radius-xs`, 1 px border in the badge colour, background = same colour at 15% alpha, text in the badge colour, 10–11 px/700 UPPER.
- Mapping used so far: UNCLASSIFIED green, CUI purple, TESTING blue, DEVELOPMENT yellow.
- Small inline status words without a box ("imported" orange, "clean" green) are 11 px/500 and right-aligned in list rows.

### 7.3 Percent / score text

Bold 13–14 px coloured by threshold (`--pct-low` < 20, `--pct-mid` < 80, `--pct-high` ≥ 80). An optional `▾` caret means it is editable (drawer "5% ▾").

### 7.4 Status word

18–20 px/700 UPPER. Values: ONLINE green, DEGRADED yellow, OFFLINE red, STARTING blue. Always sits under a muted 12 px label ("Web Server (PowerShell)").

### 7.5 Worker / state indicators

- **Control window: square numbered tiles,** 18×18 px, `--radius-xs`, number 10 px/700. They are laid out in two rows by odd/even, and each tile's colour shows its state. The legend above uses 8 px squares + 11 px muted labels: Idle grey, Reading green, Writing red.
- **Tool windows: round dots,** 10 px, 4 px gap, in a single row after a muted caps label "WORKERS". The legend is right-aligned with dots: Idle green, Reading blue, Writing red.
- Under the dot row, a 4 px progress track in `--border-strong`, full width.

> Note the reference uses *different* idle colours in the two variants (grey squares vs green dots). Pick one per product family and keep it. For Bridge Warden: **grey = idle, green = active, yellow = escalated, red = quarantined**, used in both places.

### 7.6 Table (row-card style)

- Header row: no background, 11 px muted UPPER, 14 px left padding to align with the cell text.
- Rows: separate cards, `--bg-row`, `--radius`, **42 px** tall, 6 px gap, no inner dividers.
- Cells: first column bold white; people/dates muted; numbers right-aligned.
- Hover: `--bg-row-hover`. Selected: 1 px `--blue` border + slightly lighter fill.
- Dense data tables (Benchmark) use a different style: flat rows 27 px tall with 1 px `--border` dividers, a grey group-header row ("100k rows · 100k per batch"), a coloured dot before the test name (blue read / orange write / green query), and the metric column in green mono, right-aligned.

### 7.7 Sidebar

- "Menu" title 20 px/700 with a collapse chevron `<` at the right.
- Item: 30 px tall, icon 16 + 10 px gap + label 13 px/600.
- Active: `--blue` filled pill, `--radius-sm`, full width minus padding, white text.
- Group header: icon + bold label + `˅` chevron, with an optional `+` at the far right. Children are indented to the label column. An empty state shows muted 12 px text ("No teams yet").
- 20 px gap between groups.

### 7.8 Detail drawer

Top to bottom:

1. **Header strip** (44 px): status badge, then a breadcrumb in 12 px muted (`WORKSPACE / slug`), then a `×` close at the right. Bottom border.
2. **Title** 22 px/700.
3. **Identifier line**: slug, 12 px gap, UUID, both 10–11 px mono muted.
4. **KV grid**: two column-pairs. Each pair is an icon (muted) + label (13 px muted) | value (13 px/700 white). List values stack, with a green `+` before the first item. Rows are about 32 px apart, more for multi-line values.
5. Divider.
6. **Description**: bold 13 px title + mini `Edit` button right-aligned, then body text.
7. Divider.
8. **Stepper** (see 7.9).
9. **Stat tile row**: 5 equal tiles.
10. **Button row**.
11. **Tabs** + log panel.

### 7.9 Stepper

- Circles 32–36 px diameter, 2 px ring, number 16 px/700 in the ring colour, transparent fill.
- Connector: 2 px `--border-strong` line between circles. The last connector ends in an arrowhead.
- Above the step label: a small status word (11 px, ring colour, e.g. "Changed"). Label: 13–14 px/700 white.
- Ring colour comes from step state. The reference shows red/salmon (`#ff8a8a`) for "Changed". For other products: done = green, needs attention = red, waiting = yellow, not started = grey.

### 7.10 Stat tile

- `--bg-tile`-ish (often just a 1 px `--border-strong` box on the background), `--radius`, padding 8 px, centred text.
- Label 11 px muted, value 18 px/700 white, subtext 10 px muted ("data 86 KB", "10 imported · 13 clean").
- **Large variant** (Benchmark): left-aligned, caps label 11 px muted, value 24 px mono, coloured by meaning (READ blue, WRITE orange, QUERY green, neutral white), optional subtext. Placeholder value is "–".

### 7.11 Modal

- Width 520–640 px, `--bg-modal`, `--radius-lg`, 1 px `--border-strong`, padding 24 px. Centred over a `--scrim` that dims the whole window.
- Title 16 px/700. Neutral caps section labels inside (APPEARANCE (ALL WINDOWS), PORT).
- Inputs: 30 px tall, `--bg-inset`, 1 px `--border-strong`, `--radius-sm`, 13 px text. Placeholder `--text-dim`. **Focus = 1 px `--blue` border** (no glow).
- Selects: same as inputs with a `▾` at the right.
- Inline add row: inputs side by side, then a blue `Add` button. Under it, 11 px muted helper text that says where the data is saved ("…saved to config/users.json").
- Lists inside modals: bordered box, rows 28 px tall with 1 px dividers, row text 13 px/600, optional right-aligned muted meta (file size).
- Radio/checkbox: native-style, blue when checked. A recommendation hint goes after the label in green 12 px "(Recommended)".
- Footer: see 7.1.

### 7.12 Tabs

13 px/600. Active = `--blue` text + 2 px `--blue` underline as wide as the tab plus padding. Inactive = muted. A 1 px `--border` runs under the whole tab row.

### 7.13 Log console

- Mono 11–12 px, line height 1.6.
- Column header row in muted mono caps (TIMESTAMP, METHOD, CODE, SIZE, MS, PATH).
- Timestamp `YYYY-MM-DD HH:MM:SS` in `--log-time`. Then a small `◦` bullet in the line's colour, then the message.
- Colour by source: system pink, engine cyan, success green, warning orange, error red. HTTP methods: GET blue, POST purple. Status codes are green when 2xx.
- **Milestone rows** (SERVER STARTED) span the full width with `--log-highlight-bg` and dark text.
- Embedded log panels (drawer Activity, Transform Activity) sit in a bordered `--bg-inset` box with a thin scrollbar.
- Warning panels (Benchmark) are a bordered box with orange text.

### 7.14 Code editor

`--bg-inset`, a line-number gutter 34 px wide with muted numbers, mono 12 px. SQL keywords blue, identifiers green, operators white. Above it, a muted hint line in 11 px ("Tab / Shift+Tab indent · Ctrl+Space suggestions").

### 7.15 Canvas nodes (Studio)

- Card about 190×95 px, `--bg-row`, `--radius-sm`, 1 px border.
- Header strip 22 px: tinted with the node type colour at about 25%, with an icon, a green status dot and a bold 11 px UPPER/Title name.
- Ports are 10 px circles in the type colour on the left/right edges. Edges are 1.5 px grey curves.
- Selected node: 1 px `--blue` border all round.

### 7.16 Charts

Transparent background. Axis labels 11 px muted. Gridlines are only the canvas grid. Series 1 is `--blue` with 2 px lines and 4 px point markers. The legend sits below, centred, as a square swatch + 11 px UPPER label.

---

### 7.17 Notifications (decision and status banners)

Reference: `Screenshot 2026-10-25 230759.png` (system notice, pink) and `230817.png` (error, red).

**Every product must have this.** Whenever something needs the operator's attention, and **always when a decision needs to be acted on**, a notification drops in at the **top center of the window**, so the operator sees it without switching screens.

**Look**
- Solid fill in the meaning colour, no border, `--radius-sm`, width 480–560 px (max 90% of the window), padding 14 × 16 px.
- Message 14 px/700 in white (dark `#1b1b1b` on yellow). An optional second line is 12.5 px/400 at 90% opacity.
- `×` close at the far right: 16 px, white at 85%.
- Position: centred horizontally in the window, 24 px below the app header, above everything except modals. It slides down 8 px over 160 ms and is **fully opaque from the first frame**. Never fade a safety banner in: an unfocused or background window can freeze the animation partway, leaving it half-visible.
- Stack: newest on top, 8 px gap.

| Variant | Fill | Use |
|---|---|---|
| `system` | `--notify-system` (pink) | Lifecycle and status: "Site offline. Port 24618 released." |
| `success` | `--green` | An operator action completed: "Approved", "Ledger chain valid" |
| `warn` | `--orange` | Something degraded but handled: "Safe default applied", "Oscillation damped" |
| `error` | `--red` | Failed or critical: "The engine is not running.", "Agent quarantined", "SAFE-STOP engaged" |
| `decision` | `--badge-yellow` with dark text | **A human must decide.** Title, one-line summary, countdown bar, and the decision buttons inside the banner |

**Decision notifications**
- Hold the action buttons **inside the banner** (e.g. green `Approve` | red `Reject`, 26 px), so the operator can act from any screen.
- The buttons **arm after 750 ms** (shown dimmed until then), so a banner appearing under the cursor can't be clicked by accident.
- Show the time left until the safe default: a 3 px dark bar along the bottom edge, plus "Safe default in 32 s".
- The decision is also always shown on the main working view (for Bridge Warden, the COP), with the same buttons. The banner is a shortcut, not the only place to decide.

**Anti-flood rules (mandatory)**
1. **Auto-dismiss in 5–8 s:** system/success 5 s, warn 6 s, error 8 s, decision 8 s. Hovering pauses the timer; `×` closes immediately.
2. **At most 3 banners visible.** Extra banners queue and show as they free up. A small muted chip "+N more" sits under the stack while the queue is non-empty.
3. **Dedupe:** a banner with the same key (source + message) within 10 s doesn't stack. The existing banner shows a counter (`×3`) and its timer restarts.
4. **Rate limit:** at most 1 new non-decision banner per second. Decisions go to the **front** of the queue and bump a non-decision banner, but the 3-banner cap still applies, even to decisions. Several decisions at once must not cover the working view.
4a. **Keep banners short:** body text is cut to about 170 characters. The full detail lives on the main view's decision panel.
5. **Never lose a decision:** when a decision banner times out but the decision is still open, it collapses into a persistent yellow pill at the same top-center spot: "⚑ 2 decisions pending · Review". It stays until no decisions remain. Clicking it opens the main view's decision panel.
6. **Banners are only for decisions and critical alerts.** Everything else goes to the log only: routine events, warnings, system notices, and confirmations of what the operator just did. In practice only the `decision` and `error` variants appear; `system`, `success` and `warn` are kept for products that truly need them.
6a. **Scope to the view.** When a product has several operating pictures, each picture shows only its own banners and decision cards, plus ship-wide ones (SAFE-STOP, system checks, containment). The pending pill says how many decisions are waiting on other pictures.
7. Every banner's message is also written to the log, so nothing exists only in a banner.

### 7.18 Destructive confirmation modal

Reference: `Screenshot 2026-10-25 230845.png`.

For actions that **cannot be undone** (purge, permanent delete, reset everything):
- A standard modal (7.11) with a **2 px `--red` border** and a red glow: `box-shadow: 0 0 0 1px rgba(229,72,77,.35), 0 0 28px rgba(229,72,77,.35)`.
- Title 16 px/700 → question 14 px/700 → **"WARNING:"** 14 px/700 in `--badge-red` → consequence text 13 px in `--badge-red`.
- Typed acknowledgement: muted 12 px label "Type I Acknowledge to continue", then an input (focus = 1 px `--blue`). The destructive button stays **disabled** until the text matches exactly (case-insensitive).
- Footer 50/50: the destructive action on the left (disabled grey until armed, then `--red`) | `Cancel` grey on the right. `Esc` = Cancel. Focus starts in the input.

## 8. Behaviour & motion

- Transitions 120–160 ms ease-out on hover/selection. Drawers slide in 200 ms. No bounces.
- Live numbers update in place without animation, with no layout shift (use `font-variant-numeric: tabular-nums`).
- Destructive actions confirm with a modal; irreversible ones use the typed-acknowledgement modal (7.18). Global stop actions use a **hold** gesture or a modifier key instead.
- Decisions and important events raise a notification (7.17) and follow its anti-flood rules.
- Logs auto-scroll and pause on hover.
- `Esc` closes the topmost modal or drawer.
- Every window remembers its theme from the shared setting.

---

## 9. Voice & labels

- Buttons: Title Case verbs ("Purge Temp Data", "Open Module").
- Section labels: UPPER noun phrases.
- Helper text: plain, says what happens and where ("Enter Last, First, MI. Shown as: Last, First M. Type — saved to config/users.json").
- People are written as `Last, First M. TYPE` ("Staggs, Christopher C. CIV").
- Dates: `Oct 25, 2026, 14:23` in UI, `2026-10-25 14:23:59` in logs. 24-hour clock everywhere.
- Versions: `v.YY_MMDD_HHMM`.

---

## 10. Bridge Warden mapping (how this guide applies)

| Guide pattern | Bridge Warden use |
|---|---|
| Control window | Warden Control: red BW logo, Operator / Uptime / Sim Time, Warden Core + Sim Engine status words, agent tiles, SAFE-STOP hold button |
| Big state button | Red **SAFE-STOP (Hold 2s)** while running; green **RESUME OPERATIONS (Hold 2s)** while stopped |
| Blue / orange action grids | GOVERNANCE ACTIONS / SCENARIO ACTIONS |
| Main app + drawer | Agents, Intents, Conflicts pages |
| Stepper | 8-stage governance pipeline (Auth · Authz · Cnstr · Quota · Confl · Arbit · Apprv · Exec) |
| Badges | Status, autonomy, severity, policy |
| Percent text | Trust score |
| Stat tile row | Intents/min · Approved · Rejected · Escalated · Rollbacks |
| Drawer button row | Override (orange) · Constrain · Downgrade · Rollback · Pause (grey) · Quarantine (red) |
| Log milestone row | SAFE-STOP engaged, scenario start (green); quarantine / critical breach use a red variant |
| Modal | Rollback preview, Constrain sliders, Load Scenario |
| Decision notification | Every new escalation: Approve / Reject in the banner; also on the COP with the proposed course drawn on the map |
| Error notification | Agent quarantined, critical CPA, engine critical, SAFE-STOP engaged |
| Warn notification | Safe default applied (escalation timed out), oscillation damped |
| Destructive modal | Reset All (wipes scenario, agents and ledger) |

## 11. Checklist for any new screen

- [ ] Uses only tokens from §3, with no raw hex values in components
- [ ] Exo 2 for UI, Syne Mono for data
- [ ] Logo tile colour matches the window archetype
- [ ] Every coloured state has a text label
- [ ] Buttons follow the variant table, destructive last
- [ ] Tables use row cards; selection opens a drawer and compresses columns
- [ ] Modals: scrim, title, neutral caps sections, footer per §7.1
- [ ] Readable at 1280×720 on a projector (bump `html { font-size }` if not)
- [ ] Any decision the operator must make raises a decision notification and can be acted on from the main working view without switching screens (7.17)
- [ ] Notifications follow the anti-flood rules: 5–8 s, max 3, dedupe, rate limit, pending pill
- [ ] Irreversible actions use the typed-acknowledgement modal (7.18)
