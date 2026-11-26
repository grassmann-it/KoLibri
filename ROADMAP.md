# KoLibri roadmap

## Goal

A KoLmafia-like client for Kingdom of Loathing, built on a .NET library you can program against.

1. **Play by hand first.** Log in, see the character, adventure through the browser tab and the encounter view,
   eat, drink and use items, buy from the mall, send KMail.
2. **Simple auto-combat farming.** Run N turns in a zone with a combat strategy, stop on a choice, a defeat or a
   HP threshold.
3. **LibKolibri is the product: write your own tool with it.** The library ships game primitives — session,
   requests, adventures, fights, choices, items, mall, KMail, data — with a small, documented public API.
4. **We do not ship finished solutions in the library.** Diet optimizers, arbitrage, shop CRM, analytics or a
   buffbot are things you build *with* LibKolibri. Some exist today as experimental extras in the apps; they
   stay hidden by default and are not part of the library's promise.
5. **The CLI, the MCP server and the desktop app are consumers of the library**, not where features start.

## Credits and data sources

- [KoLmafia](https://github.com/kolmafia/kolmafia): the data files (items, monsters, effects, locations,
  modifiers, concoctions) and the reference behaviour we compare against. Thank you.
- [Pricegun](https://pricegun.loathers.net/): mall price history. Thank you to the pricegun maintainers.
  We read it politely (cached, rate limited) and always show where a price came from.

See [NOTICE.md](NOTICE.md).

## 0.4.0 — Early Alpha (this release)

Cut, don't build. What must work live on a real account:

- Login and reconnect; charpane / status with correct organ limits.
- Inventory with use, eat, drink, chew; cast, rest, equip.
- Manual adventuring through the browser tab and the encounter view: fight actions, choices, turtle encounters.
- A simple auto-combat batch in the desktop: N turns in one zone, stop on a choice, a defeat or a HP threshold.
- Mall search and price-guarded buying; sending KMail.

Extras are hidden, not deleted:

- Desktop: arbitrage, clan/stash and farm metrics are not shown.
- CLI: arbitrage, bribe, snatch-plan, CRM, categorize and workflow-plan commands need `--experimental`.
- MCP: only the core tools (status, inventory, adventure, choice, item use, mall buy) are offered unless the server
  runs with `KOLIBRI_EXPERIMENTAL=1`.

The library is usable from a source checkout but its public surface is still large and will change in 0.5.
Known gaps are listed in [CHANGELOG.md](CHANGELOG.md).

## 0.5 — Library first, then NuGet

1. **One core package.** Merge `KoLibri.Core` and the `LibKolibri` façade into one package `KoLibri`; delete the
   old runtime/gateway layer.
2. **A small public API.** Shrink the public surface from roughly a thousand types to a low three-digit number.
   Delete unused types and aliases; guard the surface with `PublicAPI.txt` / ApiCompat.
3. **Accounts by id or name** instead of the fixed `Main` / `Mallbot` roles.
4. **New primitives:**
   - raw page access (`GetPageAsync` / `PostAsync`) that still goes through the session and the rate limiter,
   - `AdventureAsync(zone)` returning an encounter: fight, choice or non-combat,
   - `ICombatStrategy` (like `IChoiceResolver`) and KoL combat macros,
   - events: request, encounter, fight ended, item acquired, status changed,
   - equip and outfits, familiar, reading KMail.
5. **One adventure runner** instead of several parallel stacks.
6. **Data in the package.** The KoLmafia data as an embedded resource or a `KoLibri.Data` package, plus a
   `DataDirectory` option.
7. **Policy out of the core.** Approval tokens, grants, usage ledger and the audit hash chain move to an optional
   safety package or into the MCP server. Market extras and daily routines move into tools or stay experimental.
8. **Cookbook** of three to five small apps: print the status, a farm loop with your own strategy, a mall buy under a
   price ceiling, a KMail bot. `ConsumerSmoke` builds exactly these against the packed NuGets.
9. **Dogfooding.** CLI, desktop and MCP use only the public library API.
10. **Publish on nuget.org.** The library package first; the CLI and MCP server as dotnet tools.

## After 0.5 — towards KoLmafia parity

1. State tracking: session state, quest log, counters and wanderers.
2. Choice defaults from KoLmafia data; CCS-like combat scripts.
3. Moods and buff upkeep, HP/MP restore, equipment rules.
4. Crafting, coinmasters, closet / storage / stash.
5. Relay overrides and a scripting bridge (limited ASH/JavaScript or library equivalents; to be decided with users).

Maximizer, diet planning, arbitrage and CRM stay example apps outside the library.

## 1.0

An API stability promise for the library, a real-account verified test matrix per supported workflow, and a
documented list of what is intentionally not covered.
