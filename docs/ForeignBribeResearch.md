# Foreign-Bribe and One-Day-Ticket Research

## Goal

A `Bag of Foreign Bribes` is treated as a source of charter currencies.
Before a purchase, the tradeable shop spends per currency are compared against
Mall and Pricegun. Non-tradeable gear stays out of the arbitrage ranking.

The KoLmafia data serves here as the catalog reference for costs and item IDs:
[`coinmasters.txt`](https://github.com/kolmafia/kolmafia/blob/main/src/main/resources/data/coinmasters.txt)
and [`shoprows.txt`](https://github.com/kolmafia/kolmafia/blob/main/src/main/resources/data/shoprows.txt).

## Tradeable Candidates

| Currency | Candidates | Cost |
|---|---|---:|
| Beach Buck | History's Most Offensive Jokes, Now You're Cooking With Grease, Sloppy Seconds Diner Employee Handbook | 300 |
| Beach Buck | industrial strength anti-fungal spray | 15 |
| Beach Buck | one-day ticket to Spring Break Beach | 100 |
| Coinspiracy | mercenary pistol, mercenary rifle | 50 |
| Coinspiracy | specialty ammo bandolier | 1 |
| Coinspiracy | Merc Core Field Manuals | 300 |
| FunFunds | Scratch-and-Sniff Guide, Trash, a Memoir, Dinsey Maintenance Manual | 50 |
| FunFunds | Dinsey tattoo kit | 100 |
| FunFunds | one-day ticket to Dinseylandfill | 20 |
| Volcoino | Lava Miner's Daughter, Psycho From The Heat, The Firegate Tapes | 10 |
| Volcoino | "DEBBIE" tattoo kit | 30 |
| Volcoino | one-day ticket to That 70s Volcano | 3 |
| Wal-Mart gift certificate | To Build an Igloo, The Chill of the Wild, Cold Fang | 150 |
| Wal-Mart gift certificate | shoulder-warming lotion | 5 |
| Wal-Mart gift certificate | iceberg lettuce, ice wine | 15 |
| Wal-Mart gift certificate | one-day ticket to The Glaciest | 50 |

## Ranking

KoLibri now exposes this as a read-only command:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- bribe-rank \
  --role mallbot --item 8691 --refresh
```

Pass repeated `--currency-value CURRENCY:MEAT` options when a currency
opportunity cost is known. The live report is written to
`reports/bribe-rank-2026-11-16.json`.

The unopened pilot budget is previewed separately:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- bribe-plan \
  --role mallbot --quantity 40 --reserve 1000000 --refresh
```

For each candidate KoLibri should cache:

1. Mall lowest price, available quantity and second/third listing.
2. Pricegun recent sales, median, weighted average and sale count.
3. Tradeability, currency cost and available currency balance.
4. Ticket cost and expected farm yield when the purchase requires a ticket.

The first buy candidates are items where the Pricegun median is above the Mall
floor, at least two Mall listings exist near the floor, and expected proceeds
minus the currency opportunity cost remain positive after a safety margin.
One-listing floors and non-tradeable gear are review-only. Purchases need an
explicit preview and commit step with a daily Meat and quantity limit.

`Bag of Foreign Bribes` is therefore a currency-input opportunity, not an item
to consume blindly: record the currency received, attach it to the matching
coinmaster catalog, and rank the best sellable output after each opening.
