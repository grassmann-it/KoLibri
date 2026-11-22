# Desktop app vs. KoLmafia's GUI: alignment plan

Status of this note: written from a visual pass of the desktop app under a private Xvfb
(screenshots in `docs/images/qa/`, 820x700 and 1280x900, light and dark, fixture data only)
and from general knowledge of KoLmafia's public GUI (charpane, Item Manager, Gear Changer,
Mall Search/Purchase, Adventure selector, Session log, Preferences). KoLmafia's wiki was not
re-read in this pass; check feature details there before starting a package.

## How the screenshots are made

```bash
KOLIBRI_QA_XVFB=1 xvfb-run -a -s "-screen 0 1280x900x24" \
  dotnet run --project tools/KolLibri.UiQa -- docs/images/qa [--only=<name part>]
```

`tools/KolLibri.UiQa` opens `MainWindow` with in-memory accounts (no stored session, no
network), a temp settings file and fixture inventory/mall data, clicks through every tab and
renders each top level to a PNG with Avalonia's `RenderTargetBitmap`. It refuses to start
without `KOLIBRI_QA_XVFB=1` so it is never run on a real desktop by accident. For a clean
HOME also set `HOME`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME` to an empty directory.
Limit: renders only the window content (no title bar / OS chrome) and cannot show focus rings
or hover states.

## Alignment table

Ownership: each package names the files it may edit; packages in the same wave do not share files.
`MainWindow.axaml` is shared by many pages, so work that needs it is first split (package W0).

### MUST for 0.4.0

| # | KoLmafia feature | KoLibri today | Gap | Work package | Files (owner) | Size | Risk | Acceptance |
|---|---|---|---|---|---|---|---|---|
| W0 | n/a (enabler) | One 1000-line `MainWindow.axaml` holds Adventures, Daily, Chat & Mail, Settings panels | Parallel workers collide in one file | Split each page into a `UserControl` (`AdventuresView`, `DailyView`, `ChatMailView`, `AccountsView`) with unchanged names/bindings | `MainWindow.axaml`, new `*View.axaml(.cs)` | M | Medium: `FindControl` names move; the X11 replay tests look up controls by name | Existing X11 tests pass; screenshots unchanged |
| M1 | Charpane (HP/MP, adv, meat, fullness/drunk/spleen, familiar, effects) | Left sidebar with the same fields; shows dashes until sign-in | Not live: no refresh interval, no effect durations, no "adventures left" colouring, no mood/familiar weight | Charpane data binding: effects with turns, familiar with weight, refresh after each action | `MainWindow.Sidebar.cs`, new `CharpaneViewModel.cs`, sidebar block of `MainWindow.axaml` | M | Low (read-only data) | After a status read all fields show values; effects list shows durations; fixture test |
| M2 | Item Manager (filters, Use/Closet/Mall/Autosell per row, quantity) | Inventory list with category chips, detail pane, closet read, "acquire" box | No per-row actions in the list, no storage/closet/display-case source tabs, no multi-select | Item Manager v1: sources (Inventory / Closet / Storage) as tabs, row context menu (Use, Mall search for this item, Copy name); no selling | `InventoryViewModel.cs`, `ItemManagerViewModel.cs`, inventory block of the Inventory view, `DesktopLiveDataProvider.cs` (read members only) | L | Medium (data provider shared with Mall) | "Mall search for this item" jumps to Mall with the term filled and searches; closet/storage tabs read-only fetch |
| M3 | Gear Changer (slot grid, outfit list, familiar, "best" per modifier) | "Gear and Familiar" page: Read equipment, slot picker, ID text boxes | Raw IDs instead of pickers; no slot grid | Gear page: slot grid from equipment read, outfit and familiar dropdowns fed from the catalog; buttons share one row style | gear block of the Inventory view, `ItemManagerViewModel.cs` | M | Medium (acts on the account: equip needs the existing confirmation path) | Pickers show names; equipping still goes through the existing action gate |
| M4 | Mall Search / Purchase (term, results table, buy dialog, limits) | Search, offers table with Buy, confirmation window, Pricegun column | Good; missing: column sort, copy shop link, "show only buyable", Enter to confirm in dialog, narrow-window layout | Mall polish: sortable columns, shop name never truncated at 820 px (wrap to two lines), dialog default/accept buttons | `MallSearchView.axaml(.cs)`, `MallSearchViewModel.cs` (UI parts only), `MallPurchaseDialog.cs` | S | Low. Do not touch `Core` mall/purchase/rate-limit files | Screenshots at 820 px show full shop names; keyboard: Enter checks, Enter again confirms only when the price is checked |
| M5 | Adventure selector (zone tree, location combo, "adventure N times", condition field, auto-stop) | Filter box + three lists (locations, monsters, actions), Start/Submit/Recheck/Fight/End, batch turns | Layout cramped (buttons wrap, text box clipped, fixed 270 px lists); no stop conditions beyond item ID; no zone grouping | Adventure page re-layout: location combo with zone grouping, turn count, stop conditions (item owned, HP below, turns), run summary panel | adventures view (after W0), `AdventureCombatViewModels.cs`, `BoundedAdventureRunViewModel.cs` | L | High (drives game actions; keep all existing gates) | Same gates as today; new conditions covered by unit tests; no change to request pacing |
| M6 | Session log (every request and result in a text area, save/copy) | Collapsible bar with entries, Copy, Clear | No filter by kind, no save to file, bar permanently eats ~50 px | Log as a bottom drawer that remembers its height; kind filter; "Save…" | `SessionLogViewModel.cs`, session-log block in shell XAML | S | Low | Drawer height persisted; file contains the same text as Copy |
| M7 | In-app browser on the relay (KoLmafia's built-in KoL browser) | "Browser" placeholder removed from the four tabs; `kolibri relay` exists as a separate process | No browser page | See "In-app relay browser" below | new `BrowserView.axaml(.cs)`, new `RelayHost.cs`, `docs/desktop-browser.md` | L | High (security: token handling) | Opens the relay URL in a web view; foreign origins still 403; token never logged |
| M8 | Preferences (tabbed, search box, per-setting help) | One long Settings page with three groups, Save button | No grouping tabs, no reset to defaults, no per-field help or validation hints | Settings page as grouped sections with "Reset" and inline hints; account sign-in moved into its own group at the top | `SettingsView.axaml(.cs)`, `SettingsViewModel` (in `DesktopSettingsStore.cs`), `AccountsView` after W0 | M | Low | Reset restores documented defaults; Save status visible without scrolling |

### LATER

| # | KoLmafia feature | Gap | Proposed work | Files | Size | Risk |
|---|---|---|---|---|---|---|
| L1 | Tabbed in-app chat with channel tabs and highlights | Chat sits in an expander on "Chat & Mail" | Own page with channel tabs | `ChatMailView`, `MainWindow.ChatAndClan.cs` | L | Medium |
| L2 | Scripts / CLI box (`ash`, one-line commands) | None | Command box wired to the existing mafia-read script runner (read-only commands only) | new `CommandView`, `Core` script runner (read) | M | Medium |
| L3 | Restore settings (HP/MP restore thresholds, mood) | Only combat HP-stop | Restore/mood settings | settings + combat VM | L | High |
| L4 | Maximizer (`maximize <modifier>`) | Only "Compare selected slot" | Modifier maximizer on the Gear page | `ItemManagerViewModel.cs` | L | Medium |
| L5 | Skills tab with cast buttons | "Skills and Effects" is read-only | Cast with MP cost and confirmation | skills block of the Inventory view | M | High |
| L6 | Compact mode (charpane only) | None | Narrow window layout, hidden sidebar below 820 px | `MainWindow.Sidebar.cs`, shell XAML | M | Low |
| L7 | Zone/monster/item data pages ("Wiki" lookups) | Catalog exists in `external/kolmafia-data` but only used by pickers | Read-only data browser | new view | M | Low |

## In-app relay browser (package M7)

> Implemented in package G1; see `docs/desktop-browser.md` for the engine decision and platform caveats. The notes below are the original design.

Hosting the relay as the Browser page, keeping the relay's own security model:

1. **Process model.** The Browser page starts `kolibri relay` in-process (class `RelayHost` that wraps
   the existing relay server, bound to `127.0.0.1` on a free port) instead of shelling out. The per-run
   secret URL is created by the relay as today and kept only in memory.
2. **Token hand-over.** The page never shows or logs the URL. It navigates the web view to the printed
   `http://127.0.0.1:PORT/<secret>` URL; the relay answers with its `SameSite=Strict` cookie, which the web
   view stores in its own profile. No cookie is set from the app side. If the web engine can run in
   "private/ephemeral" mode, use it, so the cookie dies with the app. Restarting the relay creates a new
   secret, so the page must re-navigate on restart; stale cookies fail closed with 403.
3. **Engine.** Hide it behind `IGameBrowser` (navigate, back/forward, current URL) as already proposed in
   `docs/desktop-browser.md`. Phase 1: the optional plugin project with the lightest engine that works on
   the user's platform (WebKitGTK/WebView2/WKWebView wrapper); fall back to a button
   "Open in my browser" that launches the secret URL in the default browser (still safe, because the relay
   accepts only the cookie-bound browser that opened the URL first).
4. **Session sharing.** The relay already uses the stored account session on the server side, so the web
   view needs no KoL cookie and no password. Page title and URL (path only, never the secret) go to the
   session log.
5. **Security checks (acceptance).** (a) a request from another origin or without the cookie gets 403;
   (b) the secret never appears in the log, window title or crash text; (c) navigating the view away from
   `127.0.0.1:PORT` is blocked and opens the system browser instead; (d) closing the page stops the relay.
6. **Not verified.** No web view package was installed or run in this pass; engine choice still needs a test
   on Linux (GTK3 present?), Windows and macOS.

## Findings from the visual pass (fixed in this PR unless marked open)

Fixed: page widths differed per tab (content block was centred at its own natural width, 594 to 880 px) so
title and cards jumped when switching tabs; Settings page had a duplicate title, was left-offset against the
header and had its own scroll area; sidebar HP/MP bars drew full-width bands; list backgrounds were Fluent grey
on a cream theme; plain buttons (Buy, closet/gear buttons, dialog buttons) were grey and looked disabled; numeric
fields clipped their value; "Alle" (German) in the inventory filter; Buy tooltip still said purchase is not wired;
mall table clipped the Buy column and truncated shop names at 820 px; session-log expander drew its own box.

Open: Gear page rows mix text boxes and buttons with different heights (see M3); the Adventures page is long and
its three lists use a fixed 270 px height (M5); empty states are text lines at the top of cards, not centred
(works at current density; a centred illustration state is a LATER polish item); focus rings and tab order
were not checked because the renderer cannot show them (do this by hand once).
