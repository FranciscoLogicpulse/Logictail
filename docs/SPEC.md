# Logitail — feature spec

Logitail is a from-scratch Windows desktop app for personal use: a real-time log viewer in the spirit of BareTail, with a built-in network scanner tab modeled on Advanced IP Scanner's feature set.

## Clean-room rule

Both reference programs were studied **statically** with REA (strings, imports, resources, installer inventory). Neither was run. This spec records *what they do*; Logitail implements it independently.

Lines marked **(design)** are not observed in the binaries: they are Logitail decisions or inferences, and can change.

- No decompiled code, icons, bitmaps, templates or data files from either program go into Logitail.
- Names, branding and look are Logitail's own. The UI gets a full redesign at the end.
- The MAC vendor list comes from the public IEEE OUI registry, downloaded only with the owner's OK.

## Evidence sources

| Program | File studied | Findings came from |
|---|---|---|
| BareTail 3.50a (2006, Delphi, 32-bit) | `baretail.exe`, SHA-256 `160d6a3b…` | 816 strings via Ghidra (menus, dialogs, settings keys), plus a raw scan for strings under 5 characters that Ghidra skips; imports; resources. Not packed (entropy 6.5). Menus are built in code, so there are no dialog/menu resources. |
| Advanced IP Scanner 2.5.4594.1 (2022, Qt 5, 32-bit) | Inno Setup installer, SHA-256 `26d5748f…`, unpacked with 7-Zip; `advanced_ip_scanner.exe` (`4b036cc9…`) and `advanced_ip_scanner_console.exe` | 4,790 strings via Ghidra (UI text, Qt slots and action names, settings keys), console help text, payload file list, details-panel template. |

---

## Part 1 — Log viewer (from BareTail)

### 1.1 Files and tabs
- Open one or many files (Open dialog with multi-select; drag and drop onto the window).
- One tab per file. Tab label shows the file name and its size (B / KB / MB / GB with one decimal).
- Tab status per file: unknown, not changed, **changed** (new data since last viewed), error (e.g. file missing, access denied, sharing violation).
- Tab strip position: right / left / bottom / top; orientation vertical / horizontal; width 100–200 px; show tabs: never / at least two / at least one / always.
- Tab context menu: Close, Close Others, Close All.
- Recent files list (configurable max) and recent folders.
- Session restore: reopen last open files and the active tab.

### 1.2 Viewing and tailing
- **(design)** Files are never loaded whole: must handle multi-GB files and files that keep growing.
- Polling update interval: 10 min, 5 min, 1 min, 30 s, 20 s, 10 s, 5 s, 2 s, 1 s, 500 ms, 250 ms, 100 ms, 50 ms, 10 ms.
- **Follow tail** per file (auto-scroll as lines arrive).
  - Auto-activate: never, or on scroll to end.
  - Auto-deactivate: on scroll or selection, on selection, or on scroll.
- **(design)** Handles truncation and rotation (file shrinks or is replaced): reset and continue.
- **(design)** Opens files that other programs hold open for writing (shared read). BareTail has a "Sharing violation" error, so it at least reports this case.
- Encodings: ASCII, UTF-8, UTF-16 LE ("Unicode"), UTF-16 BE, selectable per file. **(design)** Auto-detect from BOM.
- Null bytes shown visibly as hex (`[00]`).
- Wrap lines at: 40, 60, 80, 100, 120, 132 columns, Maximum (off) or Auto (window width).
- Tab width: 10, 12, 16 characters (and likely smaller values).
- Line markers in the gutter (show/hide).
- Font name and size; line spacing and line offset in pixels. Default monospaced font.
- Copy selected lines (Ctrl+C).

### 1.3 Highlighting rules
- Ordered list of rules (add, delete, move up/down).
- Each rule: match string, ignore case, invert match, foreground color, background color, bold, italic.
- Named colors: Black, White, Red, Green, Blue, Yellow, Cyan, Magenta, Dark Red, Dark Green, Dark Blue, Dark Yellow, Dark Cyan, Dark Magenta, Gray 1–7, plus Custom (system color picker).
- A matching line is drawn with the rule's style. **(design)** First matching rule in list order wins; BareTail's precedence was not confirmed.
- *Logitail extension:* optional regex match mode (BareTail free only does substring matching).

### 1.4 Window and layout
- Always on top; hide main menu; remember window position, size and state.
- View presets for tiling several Logitail windows: top half, bottom half, top/middle/bottom third, automatic size.
- Command line: open files given as arguments; `--tile-window-count`, `--tile-window-index`, `--window-position`, `--window-state`.
- Help menu items worth keeping in some form: command-line usage.

### 1.5 Settings and persistence
- Options dialog covering everything above, with Restore Defaults.
- Save settings on exit: never / to file in working directory / to file next to the app / to user profile.
- Load/save highlighting and preferences to and from a file.
- *Logitail change:* settings are a JSON file in `%APPDATA%\Logitail`, not the registry.

### 1.6 Left out on purpose
- Splash screen, nag text, "Pro" upsell, website/registration links, update pinging.
- BareTailPro features (search, real-time filtering) are **not** in BareTail free, but are good candidates for Logitail later.

---

## Part 2 — Network scanner tab (from Advanced IP Scanner)

### 2.1 Scan input
- IP range text box. Accepts single IPs, ranges and lists, e.g. `192.168.0.1-100, 192.168.0.200`; also host names.
- Quick fill: this machine's subnet; a class C subnet.
- Scan from file (one IP, range or host name per line; report the line number of bad entries).
- Start / Stop / Pause / Resume. Progress: `%`, alive, dead, unknown counts.
- Speed setting (fast vs high-accuracy), thread count, timeouts.
- **(design)** Default scope: the user's own subnet.
- Console mode (separate exe in the original): `/r:<range>` or `/s:<file>`, `/f:<output file>`, `/v` to include services. **(design)** In Logitail: `logitail scan …` CLI subcommand, low priority.

### 2.2 Per-host discovery
- Alive check: ICMP echo, plus ARP for local-subnet hosts.
- MAC address (ARP) and manufacturer from MAC prefix (OUI lookup).
- Host name via reverse DNS; NetBIOS name and workgroup (with charset option).
- Services detected: HTTP (with page title), HTTPS, HTTP on 8080, FTP, RDP. (SSH and Telnet appear only as client launchers, not scan options.)
- Shared folders and printers (SMB).
- Needs Windows credentials on the remote host ("Use Windows authentication", user name): logged-on user, OS, date/time/time zone, admin shares. **Scope question for the owner.**
- Device type labels exist (router, printer, access point, media device, game console, load balancer, proxy…). **(design)** How the original assigns them is unknown; Logitail would guess from vendor and open services.
- Remembered data across scans: user-edited names, comments, MAC addresses.

### 2.3 Results view
- Columns: Status, Manufacturer, MAC address, Comments observed; Name and IP from the details panel **(design: as columns)**. Show/hide columns; sortable.
- Filters: show alive, show dead, show unknown.
- Tree rows: expand a host to see its services and shares; expand/collapse all.
- Details pane for the selected host: status, OS, IP, MAC, manufacturer, NetBIOS, user, type, date, comments, and a service table.
- Search box over results ("No items match your search").

### 2.4 Host actions (context menu)
- Explore shares (opens Windows Explorer at `\\host`), Copy path.
- Open HTTP / HTTPS / FTP in the default or a configured client.
- Connect via RDP; SSH and Telnet with configurable client programs.
- Ping and Tracert (open console tool).
- Wake-on-LAN (magic packet to the stored MAC).
- Edit comment; edit MAC; copy cell value.
- Add to / remove from favorites.

### 2.5 Favorites and history
- Favorites list as its own tab; scan only favorites.
- Rescan. Favorites save/load as XML.
- Delete stored data (names, comments, MACs) for all hosts.

### 2.6 Export
- Export all or selected results to CSV (option: comma vs semicolon delimiter) or HTML. The console version can also write XML.
- Print and print preview are low priority.

### 2.7 Left out on purpose
- Radmin integration (install/launch Radmin Viewer/Server, Radmin ports), remote shutdown, send message, update checks, language packs, bug/feature report links.

---

## Part 3 — Port scanner (from Advanced Port Scanner)

Studied statically with REA: `Advanced_Port_Scanner_2.5.3869.exe` from the official site (Famatech signature valid,
SHA-256 `d0c1662c…`), unpacked with 7-Zip, `advanced_port_scanner.exe` (`8b9c7d25…`, 4,942 strings, compared against
the IP Scanner's to isolate what's new) and the console exe's help. Not run.

### 3.1 Observed
- Port list input mixing single ports, ranges and protocols: `80, 443, UDP: 1-10`, `1,2,UDP:1-10`, `1-20`.
  Invalid input is rejected ("Invalid port range").
- Presets: well-known TCP ports `1-1023`, all TCP ports `1-65535`.
- Remembers recently used port lists.
- Results per host: open port, protocol (TCP or UDP), service name and the version of the program on the port.
- Same host discovery, favorites, comments, export (XML/HTML/CSV), print and Radmin extras as Advanced IP Scanner.
- Console mode: `/r:<range>` or `/s:<file>`, `/p:<ports>`, `/f:<output file>`.
- Service versions come from a compressed probe database (`service_probes`); its format wasn't decoded.

### 3.2 Logitail design
- Part of the Network screen, not a separate tool: an optional **Ports** field next to From/To. Empty means no port
  scan (only the built-in service checks). Filled, every live host is also port-scanned.
- **(design)** Presets menu: common ports (Logitail's own list of ~40), well-known (1-1023), all (1-65535).
- **(design)** Same syntax as the original, plus `TCP:` to switch back. Max 65,535 ports per protocol.
- **(design)** Right-click a device: "Scan ports..." for that one device.
- **(design)** Service names from a hand-written table of well-known ports; versions from the first line the
  service sends (banner), or the page title for web ports. No probe database is copied.
- **(design)** UDP: a port is "open" only if it answers, "closed" if the host reports it unreachable, otherwise
  "open or filtered". Real probes only for DNS (53), NTP (123) and NetBIOS (137); others get an empty datagram.
- **(design)** A shared limit on connections in flight so a big scan can't exhaust the PC's sockets.
- Results: an "Open ports" column, a list in the details pane, and in exports.

## Architecture

- **.NET 10**, **Avalonia** UI (cross-platform capable; Windows is the target).
- `Logitail.Core`: tail engine (line-offset index, incremental reads, encoding decode, rotation handling), highlight rules, settings. No UI dependencies.
- `Logitail.Scanner`: range parser, ping/ARP/DNS/NetBIOS probes, service checks, OUI lookup, favorites, export. No UI dependencies.
- `Logitail.App`: thin Avalonia shell, MVVM. Built to be thrown away and redesigned at the end.
- `Logitail.Tests`: unit tests for the engine and scanner logic (growth, truncation, rotation, encodings, range parsing, rule matching).

## Build order

1. Tail engine plus a minimal viewer: open, follow tail, large-file virtualized view.
2. Highlighting, encodings, options, persistence, tabs, command line.
3. Scanner engine plus a results grid.
4. Host actions, details pane, favorites, export.
5. Full front-end redesign to the owner's taste.
