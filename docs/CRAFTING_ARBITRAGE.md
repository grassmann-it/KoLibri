# Crafting-Arbitrage

KoLmafia's current [`concoctions.txt`](https://github.com/kolmafia/kolmafia/blob/main/src/main/resources/data/concoctions.txt)
is a useful recipe catalog. Each row describes an output, a crafting method
(`COOK`, `MIX`, `SUSHI`, `SMITH`, `WSMITH`, `STILL`, `ACOCK`, `SCOCK`,
`COMBINE`, and seasonal methods) and its ingredients. `items.txt` supplies
item metadata and tradeability. KoLmafia's [scripting manual](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html)
documents the corresponding `make`, `acquire`, `mallsell` and `untinker`
operations.

## Economic test

For a batch of `q` outputs, estimate:

```text
net_sale = 0.99 * conservative_sell_price * q - listing_fee
net_profit = net_sale - replacement_costs - tool_or_paste_cost
             - transfer_cost - adventure_or_skill_opportunity_cost
```

KoL's official Mall announcement specifies a 10 Meat listing fee and a 1%
cut on a successful sale. Use the independent second Mall listing or a
Pricegun median/weighted recent price as `conservative_sell_price`; never use
our own store as the market floor. Require recent sales, a minimum quantity,
fresh observations and a safety margin before recommending a craft.

Ingredient cost is the replacement value: the higher of the fresh Mall cost
and the value of an item already held. This prevents consuming a valuable
Crimbo key, food, or seasonal input merely because it is in inventory.

## Candidate families

- Cocktail and food chains: `COOK`, `MIX`, `ACOCK`, `SCOCK`, and `STILL`.
  Compare resale value with the value of using the result for the Main's own
  adventures.
- Sushi and bento chains: `SUSHI` outputs can have many variants but often
  thin liquidity; only buy ingredients after a recent-sales check.
- Smithing and upgrades: `SMITH`, `WSMITH`, `COMBINE`, and `STAFF` can turn
  cheap ore, meat stacks, or components into higher-ticket items, but capital
  and time-to-sell are high.
- Seasonal recipes: historical Crimbo toy/food rows (`CRIMBO07`, `CRIMBO12`,
  and later Crimbo recipes) are candidates for pre-season input accumulation;
  require a calendar profile and a hard inventory cap.
- Salvage and reverse transformations: `untinker`, pulverization, and other
  item conversions should compare every output route, including autosell value,
  before consuming a scarce input.

No-sell, account-bound, skill-gated, quest-gated and one-time outputs are
filtered or marked as manual review. The recipe scanner must treat the catalog
as a graph, calculate multi-step alternatives, and detect cycles rather than
assuming every recipe is executable by every account.

## Work packages

1. **CRAFT-1 catalog import** — versioned recipe snapshots, method/skill/quest
   requirements, output tradeability and seasonal tags.
2. **CRAFT-2 recipe graph** — multi-step cost propagation using inventory,
   Mall, NPC, coinmaster and opportunity-cost sources.
3. **CRAFT-3 candidate scan** — rank outputs by net Meat, percentage margin,
   capital required, sales velocity and stale-data confidence.
4. **CRAFT-4 preview/commit** — bounded ingredient purchases, craft batches and
   relists with approval, expected-state checks and post-commit verification.
5. **CRAFT-5 overstock conversion** — find excess raw inputs and half-finished
   products, while preserving configured Crimbo/adventure reserves.
6. **CRAFT-6 seasonal profiles** — Crimbo and event calendars with pre-buy,
   hold, and release thresholds instead of blind hoarding.
7. **CRAFT-7 consumption value** — price food, booze, buffs and crafting tools
   at the value they provide to Main before offering them for resale.
8. **CRAFT-8 tests and audit** — offline recipe fixtures, fee calculations,
   tradeability guards, liquidity checks and an idempotent batch ledger.

The first implementation should remain read-only: produce a table with recipe,
ingredient cost, conservative sale price, fee, expected net profit, capital,
requirements, liquidity and risk. Only the later commit adapter may buy,
craft, or list.
