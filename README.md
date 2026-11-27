<p align="center"><img src="assets/kolibri-icon.svg" width="96" alt="KoLibri"></p>

# KoLibri

**A KoLmafia-like client for Kingdom of Loathing, on a small .NET library you can program against.**

KoLibri is a .NET 8 toolset for [Kingdom of Loathing](https://www.kingdomofloathing.com/). You can play by hand
(log in, adventure, eat, drink, use items, buy from the mall), farm with a simple auto-combat loop, or write your
own tool with the library: `using KoLibri;`.

```bash
kolibri login main                                     # once per account
kolibri adventure run --location 113 --max-turns 5     # preview: sends nothing
kolibri daily run --role main                          # preview of the day's routine
```

> **Early Alpha (0.5.0).** KoLibri is usable but not stable; commands, options, file formats and the library API may
> change before 1.0. 0.4.0 was an internal milestone and was never released; its scope is part of 0.5.0.
> - **Included:** the library `KoLibri` (plus the optional `KoLibri.Safety`), the CLI `kolibri` (alias `kolicli`), the
>   MCP server `kolibri-mcp` and the desktop app `kolibri-desktop`. Release archives for `linux-x64`, `win-x64`,
>   `osx-arm64` and `osx-x64`; the two library packages are attached to the GitHub release as `.nupkg` files (not on
>   nuget.org).
> - **Unstable:** the desktop UI, MCP tool names and schemas, and the Windows and macOS builds (validated manually;
>   CI test failures there are tolerated).
> - **Live use:** needs your own Kingdom of Loathing account, one session per account (logging in elsewhere can end
>   the stored session). Requests are rate limited (actions one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`; see
>   [usage](docs/usage.md)). Anything that spends Meat or turns needs `--execute` in the CLI; AI agents must get
>   your confirmation first.
> - Details: [CHANGELOG.md](CHANGELOG.md), plans: [ROADMAP.md](ROADMAP.md). New here? Start with
>   [docs/quickstart.md](docs/quickstart.md); [docs/usage.md](docs/usage.md) shows CLI, desktop, MCP and library.

## What 0.5.0 can do

- Login and reconnect; status and charpane with organ limits.
- Inventory: use, eat, drink, chew; cast, rest, equip.
- Adventuring by hand (desktop browser tab and encounter view; CLI `adventure inspect|step|fight`) and a simple
  auto-combat loop: desktop turn batch and CLI `adventure run`, with an HP floor, a round limit, stop conditions and
  a choice policy. Turtle Tamer encounters are clicked through by default.
- Mall search and price-guarded buying; sending KMail; own shop read; pirate encryption keys.
- The daily routine (`daily run`: Day Shortener via the campfire, Hermit clovers, Internet Meme Shop).
- A library to write your own tool, plus a [cookbook](samples/cookbook/README.md) of small apps.

KoLibri is an unofficial project, not affiliated with the Kingdom of Loathing team. See [NOTICE.md](NOTICE.md).

## What is in here

| Project | Role | Package |
|---|---|---|
| `src/KoLibri` | The library: session, requests, adventures, fights, choices, items, mall, shop, KMail, events, game data | `KoLibri` |
| `src/KoLibri.Safety` | Optional guards: grants, budgets, approval tokens, audit journal | `KoLibri.Safety` |
| `src/KoLibri.Daily`, `src/KoLibri.Extras` | Daily routine and experimental extras, used inside the apps | not packed |
| `tools/KolLibri.Cli` | Command line `kolibri` | - |
| `tools/KolLibri.Mcp` | MCP server `kolibri-mcp` for AI clients | - |
| `KoLibri.Desktop` | Avalonia desktop client | - |
| `samples/cookbook` | Small apps that use only the public library API | - |
| `external/kolmafia-data` | The KoLmafia data dump as JSON Lines | - |

`KoLibri.Core` and `LibKolibri` still exist in the repository as internal implementation layers behind `KoLibri`
(the package bundles `KoLibri.Core` privately). They are not packages of their own and are going away.
The CLI, MCP server and desktop app are consumers of the library, not where features start.

Not in the library on purpose: diet planning, market strategies, shop CRM. Build them with it.

## Install

The library (the `.nupkg` files come with the GitHub release; point a local NuGet feed at the folder), or reference
the project from a source checkout:

```xml
<PackageReference Include="KoLibri" Version="0.5.0" />
<!-- or, from a source checkout: -->
<ProjectReference Include="path/to/KoLibri/src/KoLibri/KoLibri.csproj" />
```

```csharp
using KoLibri;

public static async Task RunAsync(string password)
{
    await using var kol = KolibriClient.Create();

    // First run: log in once. "main" is a free local id; later runs use OpenAsync("main").
    await using var session = await kol.LoginAsync("MyCharacter", password,
        new LoginOptions { AccountId = "main", RememberPassword = true });

    var status = await session.Character.GetStatusAsync(refresh: true);
    Console.WriteLine($"{status.PlayerName}: {status.Meat} Meat, {status.Adventures} adventures, " +
                      $"fullness {status.Fullness.Current}/{status.Fullness.Limit?.ToString() ?? "?"}");

    // Up to 5 turns in the Haunted Pantry (snarfblat 113): attack in fights, stop at choices
    // without an answer, stop early when HP drops below 30 %.
    var run = await session.Adventure.RunAsync(new RunRequest(113, 5)
    {
        Combat = CombatStrategies.AttackOnly,
        Choices = ChoiceResolvers.Safe(),
        StopWhen = new[] { StopCondition.HpBelowPercent(30) }
    });
    Console.WriteLine($"{run.StopReason} after {run.TurnsSpent} turns, {run.MeatGained} Meat");
    if (run.Open is ChoiceEncounter choice)
        Console.WriteLine($"Choice {choice.ChoiceId} is open: {string.Join(", ", choice.Options.Select(o => o.Option + "=" + o.Text))}");

    // Mall: three seal teeth (item 2), never above 500 Meat each, keep 100 000 Meat.
    var bought = await session.Mall.QuoteAndBuyAsync(new PurchaseOrder(new ItemRef(2), 3, 500) { MeatReserve = 100_000 });
    Console.WriteLine($"{bought.Status}: {bought.QuantityBought} bought for {bought.MeatSpent} Meat");
}
```

More in [src/KoLibri/README.md](src/KoLibri/README.md); the full API in [docs/lib-api-0.5.md](docs/lib-api-0.5.md).

Accounts are free local ids (`"main"`, `"mallbot"`, ...), not fixed roles. The KoLmafia data is bundled
(`external/kolmafia-data`); point the `DataDirectory` option at another copy if you need to.

Build the apps from source (needs the .NET 8 SDK):

```bash
dotnet build KoLibri.slnx
dotnet run --project tools/KolLibri.Cli -- --help
```

## CLI

Previews by default: nothing is sent without `--execute` (and `--approval-token` where it spends Meat or turns).

```bash
# Accounts
kolibri login main                         # asks for the password, stores an encrypted session
kolibri login status
kolibri login refresh --role main
kolibri token mint                         # approval token for --execute

# Reads
kolibri status --role main --refresh
kolibri inventory --role main --refresh
kolibri mall --role mallbot --item 2
kolibri shop --role mallbot

# Adventures
kolibri adventure locations --name pantry
kolibri adventure run --location 113 --max-turns 5                                      # preview
kolibri adventure run --location 113 --max-turns 5 --execute --approval-token "$TOK"    # live
kolibri adventure inspect                  # the open fight or choice, read-only
kolibri choice pending                     # exit 4 if a choice is open
kolibri choice policy list

# Items and the day
kolibri consume --role main --kind food --item ID --execute
kolibri rest --role main --times 1 --execute
kolibri daily run --role main                                         # preview
kolibri daily run --role main --only hermit --execute --approval-token "$TOK"
kolibri daily audit --tail 20
```

Also: `cast`, `keys` (pirate keys), `shop add`, `mall-reconcile`, `relay` (browse KoL in your own browser through the
stored session). Commands that were removed in 0.5 (`daily-audit`, `combat`, `autocombat`, `adventure-plan`,
`mafia-script`, `repl`) exit with code 2 and send nothing; the message names the replacement. `kolibri --help` is the
authoritative list.

Planned, not merged yet: `adventure run` farm flags (`--combat-rules`, `--recover-hp`, `--stop-item`, ...) and
`reprice` as an experimental command. <P2-W5/P3-W2: update when merged>

## Desktop

```bash
dotnet run --project KoLibri.Desktop      # or kolibri-desktop from the release archive
```

The desktop client needs a stored session per account (`kolibri login main`); it can renew stale ones at start.
It has a live character pane, inventory, skills and effects, gear, adventures (browser tab, encounter view and the
auto-combat turn batch), mall search and buy, the daily routine, an embedded browser (the relay in-process) and a
settings view. `<L3: final panel list>`

## MCP

`kolibri-mcp` serves MCP over HTTP on `127.0.0.1:8765` (local only). An AI client connects, the browser opens a
consent page, and **you** choose accounts, rights and daily limits. The client only receives what you approved; see
[docs/mcp-access.md](docs/mcp-access.md) and [docs/MCP.md](docs/MCP.md).

```bash
kolibri-mcp                    # http://127.0.0.1:8765/mcp, consent code printed here
kolibri-mcp grants list        # approved clients with today's usage
kolibri-mcp grants revoke ID   # stop one client at once
kolibri-mcp --stdio            # read-only tools over stdio, no approval needed
```

Default tools (19): `kol_status`, `kol_inventory`, `kol_login_status`, `kol_adventure_locations`,
`kol_adventure_run_execute`, `kol_combat_inspect`, `kol_combat_decision`, `kol_choice_pending`, `kol_choice_options`,
`kol_choice_chain_execute`, `kol_use_item`, `kol_consume`, `kol_rest`, `kol_price_summary`, `kol_mall_buy`,
`kol_send_message`, `kol_daily_audit`, `kol_run_daily_bridge`, `kol_meme_shop`.

Every write is two steps: the preview returns a `planId`, the same call with that id executes once, inside the
daily limits you granted. Login management is not available to AI clients.

## Safety model

1. **Reads are free, writes are gated.** An AI client needs a token you approved in the browser, with rights and
   per-day limits. The CLI and desktop are yours and need none; under an AI harness the CLI asks for a confirmation
   at the real terminal before it writes (`kolibri agent-mode`).
2. **Credentials never appear in code.** Sessions live encrypted in your local app-data folder.
3. **One policy hook in the library.** Every write goes through `IActionGuard`. The optional `KoLibri.Safety` package
   adds grants, budgets with a usage ledger, approval tokens and a hash-chained audit journal; the MCP server uses it.
4. **Bounded runs.** An adventure run stops at the turn cap, a stop condition (HP floor, item gained, ...), an
   unanswered choice, a fight round limit or a time limit, and never replays a request.
5. **Price discipline.** A purchase is submitted at the price the listing showed; if it moved, KoL rejects it and
   KoLibri does not raise the bid. `MaxUnitPrice` and `MeatReserve` are checked before and after.

## Experimental

Arbitrage, bribes, CRM and price-report commands (`arbitrage plan|buy`, `pricegun`, `crm`, `reprice`, `apply-prices`)
and the matching MCP tools are hidden by default and not part of the library's promise. Show them with `--experimental`
(CLI) or `KOLIBRI_EXPERIMENTAL=1` (MCP). They get no live verification for this release.

## Data

The complete KoLmafia data dump is bundled and typed (items 12 103, modifiers 13 375, effects 3 015, concoctions
2 238, monsters 2 366, skills 1 657, adventures 601, zones 148, and more). Refresh it from a KoLmafia checkout with
`python3 scripts/import-kolmafia-data.py`.

## Limitations

- The CLI does not cover everything the desktop and the library do: equip and item use are desktop and MCP only.
- Combat macros and KoLmafia-style combat scripts are not implemented; fights follow a strategy or a fixed action.
- Windows and macOS builds are validated by hand; Linux is the reference platform.
- The desktop client has not been live-tested broadly.
- The library API is small but not frozen before 1.0.

## Development

```bash
dotnet build KoLibri.slnx
dotnet test KoLibri.slnx
scripts/pack-lib.sh dist/nuget 0.5.0       # packs and checks KoLibri and KoLibri.Safety
```

Repository: [grassmann-it/KoLibri](https://github.com/grassmann-it/KoLibri).

## License

MIT - see [LICENSE](LICENSE).
