# Foreign Bribe Bags: research workflow

`bag of foreign bribes` is item **8691**. KoLibri treats it as a research and ranking problem. Opening bags, buying tickets, and buying coinmaster goods remain approval-gated.

| Currency | One-day ticket | Resale candidates to check |
|---|---:|---|
| Beach Buck | 100 | Spring Break books (300), anti-fungal spray (15) |
| Coinspiracy | not assumed | mercenary pistol/rifle (50), ammo bandolier (1), field manuals (300), challenge coin (1,000) |
| FunFunds | 20 | Dinsey face paint (1), food-cone/Whinskey (2), guides/manuals (50) |
| Volcoino | 3 | consumables (1), hats (5), rare books (10) |
| Wal-Mart certificate | 50 | lotions (5), ice wine/lettuce (15), books/Cold Fang (150) |

This is a catalog seed, not a buy recommendation. The current source catalogs are [KoLmafia's coinmaster data](https://github.com/kolmafia/kolmafia/blob/main/src/main/resources/data/coinmasters.txt) and [shop-row data](https://github.com/kolmafia/kolmafia/blob/main/src/main/resources/data/shoprows.txt). Only tradeable outputs are candidates; tattoos and account-bound gear are excluded.

For each currency/item pair, the ranking should calculate:

```text
expected_net = recent sale or Mall price
             - currency cost * observed currency value
             - bag cost allocation
             - safety buffer
```

The ranking prefers tradeable items with recent Pricegun volume, a healthy Mall spread, and enough observed stock. A one-day ticket is useful only when the best resale item covers its ticket cost with margin.

The actual currency distribution from a bag still needs an approved sample. Until that exists, KoLibri reports a range and refuses to turn a bag or ticket into an automatic purchase.

Planned read-only command:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- bribe-rank --item 8691 --refresh
```

It will combine Mall and Pricegun caches, show currency cost, ticket cost, expected resale, liquidity, and confidence. It will not open bags or buy anything.
