# KoLibri Workflows

`KoLWorkflowPlanner` describes the daily dry-run flow for Main and Mallbot.
Every mutating step carries the status `ApprovalRequired`; before the commit,
the adapter must re-read the account and price state.

The flow is deliberately split into phases:

1. Refresh sessions, Meat, adventures and inventory for Main and Mallbot.
2. Refresh the own shop, sales and Pricegun/Mall references.
3. Plan the Foreign-Bribe pilot batch on the Mallbot; keep the bags unopened.
4. Move unopened bags to Main and measure the currency yield there.
5. Rank tradeable Coinmaster purchases and one-day tickets by net margin.
6. Move purchased goods to the Mallbot and relist them with a price ladder and
   history; additionally review overstock as a single, rotating promo.
7. Check the Hermit stock first, subtract the utility reserve and release only
   the safe clover surplus for Mallbot/sale.
8. Run the daily Skeleton/Knucklebones target and use one-day-ticket
   opportunities only after a net-margin check.
9. Store snapshots, transfers, purchases, sales and decisions in an auditable way.

The first mutating steps remain separate: `apply-prices` can already set
explicitly confirmed shop prices; bag purchases, transfers, bag openings,
Coinmaster purchases and adventures need their own approval adapters.

The model is thus the basis for `LibKolibri`: login/HTTP, Mall, Pricegun,
inventory, adventure, Coinmaster and transfer are treated as interchangeable
adapters. The desktop app, the CLI and a later MCP server can use the same
workflow plan.

The plan can also be printed as a checklist without network access. This
matters for expired sessions and for tests; no purchases or transfers are
executed:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- \
  workflow-plan --offline \
  --main-ready --mallbot-ready \
  --meat 2409081 --bag-price 27000 --bag-stock 463 \
  --quantity 40 --reserve 1000000
```

Without `--main-ready`/`--mallbot-ready`, the account-related steps stay
deliberately `Blocked`. In normal mode, `workflow-plan` reads the Main and
Mallbot status and the bag Mall floor read-only; if that snapshot fails because
of an expired session, a blocked plan is still printed.
