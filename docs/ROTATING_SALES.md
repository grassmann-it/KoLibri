# Rotating Single-Item Offers

KoL has no native buy-order book in the Mall. The worker can therefore only
plan a **single sale opportunity** for now: one piece of an item already in
stock is offered for a short period below the visible market references. A real
buy offer remains a later, separate adapter feature.

`RotatingSalePlanner` works exclusively with snapshots. By default, at most one
proposal is produced per run. The proposal is discarded if the Mall/Pricegun
data is stale, the own store contaminates the Mall floor, no stock is
available, the cooldown is still running or the minimum margin is unreachable.
The price is bound to the most recently observed shop price and can therefore
only be changed through the existing `apply-prices --commit` path after a
fresh read.

Recommended worker cycle:

1. Read shop, inventory, Mall and Pricegun from the cache; reload only when the
   TTL has expired.
2. Build candidates with an independent second Mall listing or a Pricegun
   median; do not use own Mall listings as a reference.
3. Select the candidate with the best expected gross spread and show it as
   `ApprovalRequired`.
4. After the cooldown, check again whether the item sold and the market still
   fits. Only then may a later commit adapter relist.

The current planner does not list any goods and buys nothing. For real
single-item listings we additionally need an adapter that can split stock into
a separate listing row; a price update on an existing shop row would otherwise
affect all pieces of that row.
