# KoLibri roadmap

## Goal

A KoLmafia-like client for Kingdom of Loathing, built on a .NET library you can program against.

1. **Play by hand first.** Log in, see the character, adventure through the browser tab and the encounter view,
   eat, drink and use items, buy from the mall, send KMail.
2. **Simple auto-combat farming.** Run N turns in a zone with a combat strategy, stop on a choice, a defeat or a
   HP threshold.
3. **The `KoLibri` library is the product: write your own tool with it.** The library ships game primitives — session,
   requests, adventures, fights, choices, items, mall, KMail, data — with a small, documented public API.
4. **We do not ship finished solutions in the library.** Diet optimizers, arbitrage, shop CRM, analytics or a
   buffbot are things you build *with* KoLibri. Some exist today as experimental extras in the apps; they
   stay hidden by default and are not part of the library's promise.
5. **The CLI, the MCP server and the desktop app are consumers of the library**, not where features start.

## Credits and data sources

- [KoLmafia](https://github.com/kolmafia/kolmafia): the data files (items, monsters, effects, locations,
  modifiers, concoctions) and the reference behaviour we compare against. Thank you.
- [Pricegun](https://pricegun.loathers.net/): mall price history. Thank you to the pricegun maintainers.
  We read it politely (cached, rate limited) and always show where a price came from.

See [NOTICE.md](NOTICE.md).

## 0.4.0 — internal milestone (not released)

0.4.0 was never tagged or published. Its scope went into 0.5.0: manual adventuring, a simple auto-combat batch in the
desktop, mall search and guarded buying, KMail, and the extras (arbitrage, bribes, CRM) hidden behind
`--experimental` / `KOLIBRI_EXPERIMENTAL=1`. The 0.4 library facade `LibKolibri` is replaced by `KoLibri`.

## 0.5.0 — Library first (this release, Early Alpha)

Status: **done** = in 0.5.0, **partly** = in 0.5.0 with a stated limit, **later** = not in 0.5.0.

1. **One core package.** *Partly.* The `KoLibri` facade is the one public package and `KoLibri.Core` is bundled
   privately inside it (`KoLibri.Core` and `LibKolibri` are not packable). Folding Core into `KoLibri` and deleting
   `KoLibri.Core`, `LibKolibri` and the old runtime layer is **later** (0.5.x / 0.6), once the desktop no longer uses them.
2. **A small public API.** *Done.* About 110 public types in `KoLibri`, locked by `PublicAPI.Unshipped.txt`
   (analyzers RS0016/17/26 as errors, `check-api.sh`). At release `Unshipped` moves to `Shipped`; ApiCompat against
   0.5.0 starts with 0.5.1.
3. **Accounts by id or name** instead of fixed roles. *Done* in the library (`AccountRegistry`,
   `LoginOptions.AccountId`); the roles `main` / `mallbot` are only an app convention in the CLI and desktop.
4. **New primitives.** *Done:* raw page access through the allowlist and rate limiter, `AdventureAsync` returning
   fight / choice / non-combat, `ICombatStrategy` and `IChoiceResolver`, events, equip, mail. *Later:* KoL combat
   macros, outfits.
5. **One adventure runner.** *Done:* `RunAsync(RunRequest)` with stop conditions; CLI `adventure run` and the desktop
   batch use it.
6. **Data in the package.** *Partly.* The KoLmafia data files ship inside the nupkg and are copied next to your app
   (`KolibriOptions.DataDirectory` overrides the location). *Later:* an embedded resource or a `KoLibri.Data`
   package, and a loud diagnostic when data is missing.
7. **Policy out of the core.** *Done.* The optional package `KoLibri.Safety` (grants, budgets, approval tokens, audit
   journal) sits behind the one `IActionGuard` hook; daily routines and market extras live in `KoLibri.Daily` /
   `KoLibri.Extras`, which are not packed.
8. **Cookbook.** *Done:* five small apps in [samples/cookbook](samples/cookbook/README.md) (status, farm loop with your
   own strategy, mall buy under a price ceiling, KMail bot, events). `tests/ConsumerSmoke` is meant to build them
   against the packed nupkgs; moving it off the old 0.4.0 package references is in progress. <ConsumerSmoke: update when merged>
9. **Dogfooding.** *Partly.* CLI and MCP use the `KoLibri` API; the desktop is moving over.
10. **Distribution.** *Changed.* The two library nupkgs (`KoLibri`, `KoLibri.Safety`) are attached to the GitHub
    release, not pushed to nuget.org. nuget.org and `dotnet tool` packages for the CLI and MCP server are **later**.

Not in 0.5.0: CLI adventure farm flags and the experimental `reprice` command are still being merged
<P2-W5/P3-W2 placeholder>.

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
