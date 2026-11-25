# Logitail design

Brief: resemble LogicERP (the owner's company web app), sidebar navigation, teal accent.
Subject: a desk tool someone keeps open all day beside other work, watching logs scroll and occasionally
checking what's on the network. Primary job: make new log lines and their severity readable at a glance.

## Tokens

Taken from LogicERP's portal CSS where it defines them; the rest chosen to sit with them.

| Name | Hex | Use | Source |
|---|---|---|---|
| Steel teal | `#67929E` | accent: active nav, primary button, selection, focus | LogicERP `--portal-brand-primary` |
| Teal light | `#9BB5BC` | wrap markers, disabled accent, progress track | LogicERP `--color-sf-primary-light` |
| Teal mist | `#E8EEF0` | hover and selected rows, active sidebar item fill | LogicERP `--color-sf-primary-lighter` |
| Paper | `#FFFFFF` | header, sidebar, panels | LogicERP header/sidebar |
| Canvas | `#F3F5F6` | app background behind the panel | chosen: one step off white so the panel reads as a sheet |
| Charcoal | `#323537` | text | LogicERP login background (avoids a generic #111) |
| Slate | `#6C7A7F` | secondary text, column headers | chosen, teal-greyed to match |
| Rule | `#DEE2E6` | borders and dividers | LogicERP borders |

Status colors are LogicERP's: green `#93C949`, orange `#EA843E`, red `#E74E34`, yellow `#EFE15F`.

Type: Montserrat (regular and bold, LogicERP's face) for the interface; Cascadia Mono for log text only,
because columns of log text must align. Scale: 12 (column headers, captions), 13 (body, tables), 15 (panel titles),
17 (wordmark). Sentence case everywhere; no all-caps labels.

## Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ [≡]  ▌Logitail    File Edit View Preferences Help    [Following] │  header 56, white, rule below
├──────────────┬───────────────────────────────────────────────────┤
│ ▤ Logs       │ ┌───────────────────────────────────────────────┐ │
│   app.log  3 │ │ log text, Cascadia Mono                      │ │  canvas, 16 padding
│ ● db.log     │ │                                               │ │  one white panel, 8 radius
│   + Open...  │ │                                               │ │
│ ⌁ Network    │ ├───────────────────────────────────────────────┤ │
│              │ │ Following | 341 lines | 18 KB | UTF-8        │ │  status inside the panel
│  220 / 50    │ └───────────────────────────────────────────────┘ │
└──────────────┴───────────────────────────────────────────────────┘
```

Left aligned throughout. The sidebar replaces the file tab strip (the strip can still be turned on in Options).

## Principles

- **One bold thing:** the sidebar's file list is live. A file that grew while you looked elsewhere shows a teal
  bar and the count of new lines. That is the reason to glance at the app; everything else stays quiet.
- One panel per screen, not a grid of cards. One radius (8) for panels, 4 for controls.
- No idle motion. Lines arriving already move the log; nothing else animates.
- Highlight rules default to tinted backgrounds with dark text (readable for hours), not saturated fills.

## Review against generic defaults

- First draft had a pulsing "Following" pill in the header. Removed the pulse: the log itself shows new lines,
  and a second moving thing would compete with it. The pill stays as a static toggle.
- First draft had dashboard-style summary cards above the scan table (alive / dead / unknown counts). Dropped:
  that's the SaaS card kit, and the counts already sit in the status line.
- Kept LogicERP's soft panel shadow because the brief asks for LogicERP; limited to the single content panel.
- Status line uses plain separators, not middle-dot meta strings.

## Assets

- Montserrat font files are copied from LogicERP's portal (`wwwroot/fonts`). Montserrat is under the SIL Open
  Font License 1.1, (c) 2011 The Montserrat Project Authors. See `src/Logitail.App/Assets/Fonts/NOTICE.txt`.
- No LogicERP or Logicpulse logos, images or icon files are used. Logitail's mark and sidebar icons are drawn in code.
