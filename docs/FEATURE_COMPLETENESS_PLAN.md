# KoLibri Feature Completeness and Product Plan

This document defines the lean KoLmafia replacement that KoLibri actually
needs. KoLibri should not copy every historical KoLmafia feature. The core is a
small, testable KoL integration with safe read/preview/commit boundaries.
Arbitrage, Mall operations and repeatable daily workflows take priority over a
complete scripting ecosystem.

## Target picture

`LibKolibri` remains the transport- and UI-independent KoL domain. The domain
knows accounts, status, inventory, Mall, price sources, items, recipes,
Coinmasters, adventures and approval policies. It does not perform HTTP calls
itself.

Three equal surfaces sit on top of it:

1. **CLI:** reproducible jobs, JSON for shell and AI.
2. **MCP/Automation Gateway:** the same read and preview functions with
   structured schemas; mutations only via Preview → Approval → Commit →
   Verify.
3. **Avalonia desktop:** a control panel for the same services; no business
   logic and no own KoL requests in the ViewModel.

The desktop app gets a compact workspace with a global account/session status,
daily plan and a dense table for prices and proposals. oChan/MeowChan serves as
UX inspiration for navigation, thumbnail/status cards, dark theme and quick
detail views; KoLibri adopts no image-board domain logic.

## What KoLmafia covers and what KoLibri adopts

| Area | KoLibri goal | Priority | Decision |
|---|---|---:|---|
| Login, session, two accounts | Encrypted profiles, cookie lifecycle, expiry/refresh status | P0 | complete, early |
| Status, inventory, shop, Mall | Cached snapshots, IDs, quantities, sellers, stale display | P0 | complete, early |
| Price sources | Mall, Pricegun, own sales, history, robust reference | P0 | complete, early |
| Repricing | Proposals, minimum price, fees, conflict check, commit/verify | P0/P1 | early |
| Underprice snatcher | Watchlists, top-N, liquidity, seller blacklist, budget, preview | P1 | early |
| Purchase adapter | Small chunks, fresh re-read, approval, post-buy verification | P1 | after preview hardening |
| Main/Mallbot transfer | Item/Meat transfer, target/source check, audit, preview | P1 | early, after purchase adapter |
| Adventure planner | Knucklebones/Skeleton, daily limits, resource and goal planning | P1 | early |
| Adventure executor | Zone/fight/choice actions, checkpoints, stop on unknown state | P2 | later, conservative |
| Combat automation | Declarative rules, known combat decisions, dry run | P3 | only if needed |
| Inventory/item master | Tradeability, binding, package/open, NPC, recipe/fold/zap groups | P0/P1 | early |
| Crafting/transformation | Recipe graph, opportunity cost, failure risk, result liquidity | P2 | after item master |
| Coinmaster/Airport | Currency graph, ticket costs, opportunity cost, ROI | P1/P2 | early as preview |
| IotM/MrStore | Dynamic catalog, Mr. A numeraire, utility NPV, liquidity | P2 | later as purchase automation |
| CRM | Sales, buyer key, points, segmentation, blocklist | P1/P2 | read-only first |
| Messages/Gifts | Intent/approval objects, audit and opt-in | P3 | later |
| Scripting language | No JavaScript/CLI clone at first; typed workflows | P3 | deliberately later |
| Relay Browser | No full clone; optional embedded KoL pages/deep links | P4 | skip/optional |
| Familiar/equipment/modifiers | Data model and read-only recommendation | P3 | only for farm/ROI cases |
| Quest/path/ascension automation | Not a goal of the Mallbot | P4 | skip |

## Prioritized milestones

### M0: Trustworthy data foundation

**Dependencies:** none.

- Bring `AccountReadClient`, `KoLMallReadClient`, `PricegunReadClient` and
  `KoLShopSalesReadClient` onto shared snapshot/cache contracts.
- Item ID instead of name as the primary key; names only as display/search
  alias.
- Test source freshness, stale fallback, request gate, in-flight
  deduplication and cache invalidation.
- Item master as a versioned local artifact with parser diagnostics,
  tradeability, account-bound, package/open and explicit variant links.
- Audit event for every observation: account, source, timestamp, stale status,
  request ID.

**Acceptance criteria:** any offline snapshot can be reproduced without a
network; no proposal is based unmarked on an old or own-listing source.

### M1: Arbitrage MVP

**Dependency:** M0.

- Watchlist scheduler for Tiny Plastic, high-value, Crimbo/Elf Guard,
  Pirate-Encryption, IotM packages and Coinmaster outputs.
- `MarketPriceEstimator` and `UnderpricedPurchasePlanner` as one policy
  pipeline with fees, reserve, minimum margin, liquidity, stale and
  tradeability gates.
- `snatch-plan` and `reprice` as JSON preview; sortable desktop table.
- Seller/store blacklist, own-store exclusion and `reason codes` in every
  decision.
- Daily report: new candidates, accepted proposals, rejection reasons, capital
  tied up, expected net proceeds.

**Acceptance criteria:** the same input snapshots deterministically produce
the same ranking; no action sends a purchase/price POST.

### M2: Mutation core

**Dependency:** M1, approval contracts and audit log.

- Mall purchase: fresh listing read, bind store/price/quantity exactly, small
  chunk, budget/reserve check, commit, inventory/Meat verification.
- Shop price update: bind the expected old price, abort on conflict, then
  re-read the shop.
- Transfer: Main ↔ Mallbot as two separate preview/commit steps; no implicit
  combination with a purchase or adventure.
- Idempotency key per operation; a retry must not buy, transfer or relist
  twice.

**Acceptance criteria:** artificial price-change, sold-out, login-redirect,
budget and partial-failure fixtures lead to a safe abort and a clear recovery
display.

### M3: Daily operation and farming

**Dependency:** M0/M1; mutating adventure adapters only after M2.

- Daily Knucklebones/Skeleton plan with adventure budget, Lucky Egg
  consumption and daily/semi-rare limits.
- Farm-vs-Buy for Glaciest, Volcano, Beach, Coinspiracy, Hermit clovers,
  Pirate/Crimbo material and seasonal reserves.
- Coinmaster graph including ticket, currency and opportunity cost.
- Inventory aging: `hold`, `reprice`, `rotate-one`, `craft`, `liquidate`.
- Scheduler: observe → plan → output preview; commit stays separate.

**Acceptance criteria:** a daily run is resumable, stops at an unknown
choice/fight or stale session and produces a complete audit report.

### M4: IotM, crafting and CRM

**Dependency:** M0, M1, Coinmaster and item-master contracts.

- Dynamic MrStore catalog instead of a hardcoded monthly list.
- Evaluate tradeability and giftability separately; `t` items can be
  Mall-tradeable without being giftable between accounts.
- Utility NPV against Mr. A opportunity cost, historical sales and liquidity
  risk.
- Recipe graph for Cook/Mix/Smith/Still/Fold/Zap/Pulverize/Untinker; every
  edge with input opportunity cost and sales risk.
- CRM read-only: repeat buyers, revenue, points, segment and blocklists.

**Acceptance criteria:** no IotM/crafting proposal without an explicit source,
tradeability and a liquidity justification.

### M5: Lean KoLmafia replacement

**Dependency:** stable M0–M4 APIs and real operating data.

- Typed workflow definitions and a small plugin/provider interface.
- Known combat rules and adventure checkpoints, but no full Java scripting
  clone.
- Equipment/familiar/modifier data only where it improves farm or ROI
  decisions.
- Optional relay/browser link instead of an embedded full browser.
- Map the MCP toolset onto the same domain commands; no second policy stack.

**Acceptance criterion:** a user can start the same daily run via CLI, MCP or
desktop and gets the same plan, the same warnings and the same approval ID.

## Desktop UX boundaries

The first Avalonia version needs five main areas:

1. **Cockpit:** Main/Mallbot session, Meat, adventures, stale sources, open
   approvals and daily goal.
2. **Arbitrage:** dense table with item, stock, Mall floor, reference,
   expected net proceeds, margin, liquidity, age, decision and reason.
3. **Shop:** own listings, reprice proposals, price history, batch preview.
4. **Adventure/Farm:** goal, turns, EV/turn, farm-vs-buy, reserves, stop
   reasons.
5. **Operations:** approval inbox, audit log, blacklist, cache and worker
   status.

No ViewModel may parse KoL HTML or call `HttpClient` itself. Every view
consumes a query/command service that CLI and MCP also use. Tables need column
pinning, filters, keyboard navigation, copy-as-JSON and clear stale/preview
badges. Color highlighting must never be the only status channel; text and
icon must carry the same information.

## API interfaces

```text
IAccountQuery              status, inventory, sessions
IMarketQuery               mall, price history, own shop, snapshots
IArbitragePlanner          underprice, reprice, farm-vs-buy
IWorkflowPlanner           daily plan, dependencies, stop reasons
IItemCatalog               item facts, variants, tradeability
IApprovalService            preview, approve, expire, commit, verify
IAuditLog                  immutable operation and source events
ITransferGateway            preview/commit/verify Main <-> Mallbot
IAdventureGateway           preview first; later bounded execution
```

CLI, MCP and Avalonia may only use these contracts. Transport adapters
implement the KoL routes; LibKolibri contains the abstracted contracts and
gateway registration, Core contains the pure evaluation.

## Test strategy

- Parser/HTML fixtures for every KoL page and login redirect.
- Deterministic clock and fake transport for cache, rate limit and stale.
- Property tests for fees, price/quantity limits, idempotency and budget
  reserves.
- Golden JSON for the same snapshot input in CLI, MCP and desktop.
- Mutation tests: price changed, listing sold out, session expired,
  Meat/inventory deviating, transfer partially successful.
- Contract tests for KoL routes behind a fake server; no live purchases in CI.
- UI smoke tests for navigation, filters, approval inbox and keyboard
  operation.

## Deliberate non-goals

- no complete clone of KoLmafia scripting, Relay Browser or the Java GUI;
- no global Mall crawling loop without a watchlist and cache;
- no automatic gift/message campaign without opt-in and approval;
- no IotM/seasonal hardcoding without a dynamic source and expiry date;
- no automatic decision based on a single price outlier.


## Design system: Stickman Standard and Ops Dark

The default mode should feel KoL-inspired right away without rebuilding the old KoLmafia Java GUI. `Stickman Standard` uses a light, slightly paper-like surface, black lines, simple stick-figure silhouettes and small hand-drawn markers. Functional motifs are bones for daily goals, a Meat bag for capital, pirate flags for Crimbo/Pirate reserves and a price tag for Mall actions. No information is conveyed solely through color or drawing; text, icon and status badge stay visible together.

Semantic tokens belong centrally in `App.axaml` or a dedicated ResourceDictionary: Canvas/Ink, Panel/Raised, Text/Muted, Border/Focus, Accent, Success, Warning and Error. Meat gold, Crimbo red, adventure green and ink blue are accents, not independent page colors. `comfortable` is the default density, `compact` the density for large Mall tables. Theme switches must not lose navigation, filters or running snapshots.

`Ops Dark` is optional for long scans: dark surfaces, the same semantic tokens and the same information order. A later seasonal Crimbo accent set must not reinterpret status colors.

## oChan/MeowChan patterns for KoLibri

- Shell header with account/session badges, cache age, refresh and More menu;
- responsive navigation with five direct pages and a More flyout;
- central light/dark theme dictionaries, tokens for typography, surfaces, focus, dialogs and density;
- compact, virtualized tables with persistent columns, filters and keyboard focus;
- right inspector only after row selection: price history, top listings, stale sources, confidence and approval diff;
- non-blocking loading, empty, stale and error banners as well as a compact event stream;
- command palette and `Ctrl/Cmd+1..5`, search, refresh and approval inbox;
- focus returns to the last row or control after refresh, navigation or closing the inspector.

The current [KoLibri.Desktop/MainWindow.axaml](../KoLibri.Desktop/MainWindow.axaml) is still a static mock: hardcoded KPI values, text instead of a DataGrid, no real page ViewModels and no theme switching. UI work starts with the M0/M1 query services; oChan provides shell, focus and layout patterns, not business logic.
