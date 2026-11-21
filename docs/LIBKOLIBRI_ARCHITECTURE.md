# LibKolibri architecture

`LibKolibri` is the reusable adapter layer for KoL-facing projects. The
current desktop app and CLI can keep using `KoLibri.Core` while they move
behind the stable contracts in `src/LibKolibri`.

The boundary is split into capabilities:

- **Account**: login/session, status and inventory snapshots.
- **Mall**: mall search, own-shop reads, cached price observations.
- **Pricing**: source aggregation, Pricegun history, margin and repricing policy.
- **Adventure**: pure plans for Skeleton/Knucklebones and later execution adapters.
- **Coinmasters**: Foreign Bribe, one-day-ticket and DLC-shop catalogs with resale ranking.
- **Transfers**: preview-only Main/Mallbot item movements until an approval gateway exists.
- **BuyerAnalytics**: own-shop sales history, repeat buyers and account-link signals.

The CLI, Avalonia UI and a future MCP server should depend on these interfaces,
not on HTML parsers or KoL routes. KoL HTTP adapters remain in the integration
implementation. Every mutating capability has a preview object, expected-state
checks, an approval token, and an audit event before a commit request.

The first extraction is intentionally additive: `LibKolibri` references the
existing Core models so the running app does not need a large namespace rewrite.
Later work can move the stable records into LibKolibri and leave Core as the
KoL HTTP implementation package.

An existing host can adopt the boundary incrementally:

```csharp
var runtime = new LibKolibriRuntime()
    .RegisterCoreReadAdapters(accountReader, mallReader, foreignBribePlanner);
var mall = runtime.Get<IMallGateway>();
```

The adapters retain Core's cache and rate-limit policies, so adding LibKolibri
does not increase KoL request volume.

## Workflow layers

1. **Observe**: account status, inventory, own shop, mall, Pricegun and buyer snapshots.
2. **Plan**: adventures, repricing, transfers, ticket/currency purchases and bag batches.
3. **Review**: expected state, budget, item tradeability, cache age, confidence and audit summary.
4. **Commit**: one bounded mutation, followed by a fresh read and verification.

This gives us a small Mallbot/Mallmanager product first while retaining a path
to a KoLmafia replacement. New integrations, such as a future combat script or
coinmaster adapter, implement one capability instead of becoming UI-specific code.

## API stability

`LibKolibri` exposes interfaces and records that are safe to consume from the
CLI, Avalonia and MCP. Changes should be additive within a minor version:
introduce a new optional capability or a new interface, then adapt the existing
Core implementation. Keep KoL route names, HTML parsing, session-cookie handling
and retry policy out of these contracts. A major version is required only when
a public record changes meaning or a required method changes its semantics.

The first release deliberately reuses Core snapshot types. Once two adapters
exist, copy the stable fields into LibKolibri records and provide mapping
extensions in the Core adapter. That removes the current compile-time coupling
without forcing consumers to change their workflow code.

## Test and fixture strategy

- **Contract tests** use an in-memory gateway to verify that every workflow
  handles stale cache data, an empty mall result and a rejected mutation.
- **Adapter fixtures** are saved KoL/Pricegun responses with credentials and
  session cookies removed. Parser tests run entirely offline and cover both
  successful and malformed responses.
- **Workflow tests** assert the preview payload, budget/reserve checks,
  approval requirement and post-commit verification. They must not call KoL.
- **Smoke tests** run the CLI's `status`, `mall`, `compare` and `bribe-rank`
  paths against the offline demo adapter. Live calls remain opt-in and are
  never part of the default build.

## Work packages

- **L1 contracts**: keep gateway records versioned and add fixture adapters.
- **L2 observation**: persist cache snapshots and buyer events without credentials.
- **L3 workflows**: bag pilot, inventory reconciliation, repricing and transfer previews.
- **L4 adventure**: location/choice/condition adapters, starting with the daily Skeleton plan.
- **L5 commit adapters**: purchases, transfers and adventure execution behind policy limits.
- **L6 clients**: Avalonia dashboard, CLI and MCP all consume the same workflow services.
