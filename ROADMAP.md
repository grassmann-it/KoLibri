# KoLibri roadmap

## Goals

1. **LibKolibri is the product.** A .NET library (`KoLibri.LibKolibri`) that lets anyone build small
   applications that talk to Kingdom of Loathing: mallbots, arbitrage, daily routines, adventure runners,
   shop analytics. Stable public API, documented, no UI dependency.
2. **`kolibri` (alias `kolicli`) and `kolibri-mcp` are for automation without writing .NET.** Scripts and AI
   assistants use the same capabilities through the command line and through MCP, under limits the human
   approves (rights, Meat budgets, price ceilings, reserves).
3. **The desktop app is an example.** It shows what the library can do and is useful for manual play, but
   it is not where features start.
4. **Feature-complete with KoLmafia where that makes sense.** Data comes from KoLmafia's own data files and
   reports; we import and credit them rather than re-deriving them. Where KoLmafia's approach does not fit
   (for example embedding its scripting languages), we say so and offer a library or MCP equivalent.

## Credits and data sources

- [KoLmafia](https://github.com/kolmafia/kolmafia): the data files (items, monsters, effects, locations,
  modifiers, concoctions) and the reference behaviour we compare against. Thank you.
- [Pricegun](https://pricegun.loathers.net/): mall price history. Thank you to the pricegun maintainers.
  We read it politely (cached, rate limited) and always show where a price came from.

See [NOTICE.md](NOTICE.md).

## 0.4.0 (this release)

Library with account, mall, shop, KMail, item use and adventure sessions; CLI with agent-mode guard; MCP over
localhost HTTP with OAuth consent and per-grant limits; shop analytics and customer rewards; desktop early
alpha; packages for Windows, Linux and macOS.

Known gaps are listed honestly in [planning/RELEASE_0_4_0.md](planning/RELEASE_0_4_0.md) and
[planning/LEARNINGS_2026-11-21.md](planning/LEARNINGS_2026-11-21.md).

## Next: 0.5 (library first)

- Publish `KoLibri.LibKolibri`, `KoLibri.Core`, `KoLibri.Cli` and `KoLibri.Mcp` on nuget.org.
- Public API pass: one documented front door (`KolibriClient`), XML docs everywhere, API reference.
- Cookbook of small apps: mallbot, arbitrage watcher, daily runner, shop CRM, adventure loop.
- Move shop analytics, CRM and price suggestions from the MCP tool layer into the library.
- IotM scanner as a library call, CLI command and MCP tool.
- Consolidate the ~34 MCP tools into a smaller, clearer set.
- Buffbot building blocks in LibKolibri (so a buffbot is a small app, like KoLmafia's): cast a skill on another
  player (`runskillz.php` with `targetplayer`), poll and parse incoming KMail (requests, meat/items), reply or
  send KMail with items and meat, mood/buff list with MP budget, soulsauce/MP restore (Soul Food is available as
  `kolibri cast --skill 7185`), per-player cooldown and daily limits. Today: KMail send/read and self casts exist,
  casting on others and an inbox watcher are missing.
- Live-verify the remaining write tools (meme shop, item use, reprice, KMail, adventures).

## 0.6 (KoLmafia data and reports)

- Import of personal KoLmafia data: price and sales history, settings, session and combat logs.
- Keep the bundled KoLmafia data snapshot current (scripted import, versioned, with provenance).
- Pricegun: configurable freshness, clear attribution in every report that uses it.
- Quest, path and ascension state (planning/KOLMAFIA_PARITAET.md AP10).

## 0.7 and beyond (parity where sensible)

- Maximizer and moods, relay-style special pages, broader CLI command coverage (AP11 to AP14).
- Script compatibility: a way to run common KoLmafia workflows, either through a limited ASH/JavaScript
  bridge or through library equivalents. To be decided with users, not assumed.
- OS keychain for secrets on macOS and Linux, signed Windows and macOS builds.

## 1.0

API stability promise for the library, a real-account verified test matrix per supported workflow, and a
documented list of what is intentionally not covered.
