# KoLibri Arbitrage Work Packages

This document separates safe observation and evaluation from later purchases,
transfers and Mall changes. Every mutation needs a fresh read, a budget check,
approval, commit and verification.

## Evaluation basis

For each item, the item ID, tradeability, giftability, autosell/NPC value,
Coinmaster/recipe relations, equipment type, IotM/seasonal status, stock,
turnover and source age are stored. The expected net proceeds are:

```text
netSell = expectedSellPrice * 0.99 - listingFees - packagingCost
netSpread = netSell - acquisitionCost - transferCost - adventureCost
```

`expectedSellPrice` must not simply be the first Mall entry. KoLibri compares
a robust Mall estimator (top-k, second price, quantities, own listings
excluded) with the Pricegun median/weighted sales and the historical price. A
recommendation needs a minimum amount, a minimum margin, liquidity and a
source that is not stale.

## Priority P0: Data and scanner

- **Item catalog:** Tradeable/giftable/bound, NPC, autosell, recipe, fold/zap
  group, Coinmaster, equipment slot, IotM and seasonal tags.
- **Mall snapshot:** Top 10 listings, quantities, sellers, price age and
  `ownStoreContaminatesMarket`; the own Mall store must not be a buy reference.
- **Pricegun/history:** long TTL for the overview, short TTL for a purchase
  decision; clearly mark a stale fallback.
- **Farm-vs-Buy:** the same target stock is simulated as own farming, Mall
  purchase, crafting or Coinmaster spend.

## Priority P1: Underprice sniper

The worker scans a candidate list instead of permanently querying the entire
Mall. Candidates come from own target items, historical winners, farm/
Coinmaster outputs, IotMs/accessories and fresh Mall anomalies.

A purchase proposal needs:

1. an independent reference (Pricegun or a second/robust Mall price),
2. at least `minAbsoluteSpread` and `minMarginPercent`,
3. sufficient sales volume or a deliberately accepted holding window,
4. tradeability and no account binding,
5. daily budget, item limit and reserve for Main/Mallbot.

Before the purchase, store price, quantity, Meat and session are re-read. The
commit buys at most a small chunk and then verifies inventory and Meat.
`priceChanged`, `soldOut`, `loginRedirect` and a snapshot that is too old abort
the operation.

## Priority P1: IotM and accessory arbitrage

IotM items are split into three classes: tradeable utility items with ongoing
yield, pure equipment/collectibles and consumables. For each class, Mall value,
expected benefit, replacement cost, holding period and resale risk are
calculated separately. Mr. Accessory is only a numeraire/opportunity-cost
value; a price below one Mr. A is not an automatic reason to buy.

Do not buy if the item is account-bound, not giftable, only a single
historical outlier or without liquid sales. For utility IotMs,
`resale + retained utility` is compared against the Meat tied up.

## Priority P1: Farm and adventure arbitrage

For each zone, `expectedDropValue / adventure` is computed from current drops,
Pricegun/Mall and sales velocity. Ticket, consumables, turn opportunity cost
and target resources are subtracted. The result is compared with a direct Mall
purchase.

The first candidates are Skeleton/Knucklebones, Glaciest books and tickets,
Volcano books/tickets, Coinspiracy outputs, Pirate-Encryption/Crimbo
materials, Elf-Guard/Crimbuccaneer sources and Hermit clovers. Daily limits,
semirares, Lucky Eggs and seasonal windows are managed as their own resources;
the adventure runner stops at an unknown fight/choice, a stale session, too
little HP/turns or a reached goal.

## Priority P1: Coinmaster and event stock

All `coinmasters.txt`/`shoprows.txt` rows are stored as a graph: currency →
ticket/item → expected net proceeds. A currency gets an opportunity cost from
the best alternative spend. Purchases only make sense if the net proceeds are
positive after ticket costs, transfer, Mall fee and holding/seasonal risk.
Crimbo/Pirate stock gets its own reserve account and is not mixed with the
running Mall inventory.

## Priority P2: Transformations

Craft, fold, zap, pulverize, untinker and other recipe paths are evaluated as
a directed graph. Inputs are valued at the own-stock opportunity cost or the
Mall price; Meatpaste, daily limits, failure risk and sales velocity are added.
Gambling paths need a separate loss limit and are preview-only by default.

## Priority P2: Shop, overstock and CRM

- Stock age and quantity produce `hold`, `reprice`, `rotate-one`, `bundle`,
  `transfer` or `liquidate` proposals.
- The rotation worker presents at most one piece of genuine surplus as a
  time-limited promo; cooldown, independent price and minimum margin are
  mandatory.
- Own sales provide throughput and repeat-buyer data. Loyalty points, the
  blocklist and later message/gift intents stay local and approval-bound.

## Worker and approval boundary

```text
observe -> cache -> score -> preview -> refresh -> approve -> commit -> verify
```

Workers may read, cache and produce proposals. Buying, transfer, bag opening,
adventure, gift, message and price change remain separate capabilities with an
audit ID. A sniper must never buy from a stale cache or from its own Mall
listing.

KoLmafia references: [CLI/acquire documentation](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html),
[InventoryManager](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/session/InventoryManager.java),
[MallPurchaseRequest](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/MallPurchaseRequest.java),
[ManageStoreRequest](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/ManageStoreRequest.java),
[Pricegun](https://pricegun.loathers.net/) and [tradeability functions](https://wiki.kolmafia.us/index.php?title=Is_tradeable).
