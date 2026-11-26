# KoLibri

**Kingdom of Loathing client library for .NET 8** — one package, one namespace: `using KoLibri;`.

> **0.5.0-dev.** The API is the one agreed in [docs/lib-api-0.5.md](../../docs/lib-api-0.5.md) and is locked by
> `PublicAPI.Unshipped.txt` (PublicApiAnalyzers). Not on NuGet yet. During the transition the game logic still
> lives in `KoLibri.Core`; it is a private dependency, so only the types below are visible to you.

## Minimal example

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

The example is compiled as part of the test suite (`tests/KoLibri.Tests/ReadmeExample.cs`), so it cannot drift
from the API.

## What is in the box

| | |
|---|---|
| `KolibriClient` | one per process: accounts, limiter, game data, events. `Create(options)`, `LoginAsync`, `OpenAsync(idOrName)` |
| `KolibriSession` | one logged-in account. Facets `Character`, `Inventory`, `Adventure`, `Mall`, `Shop`, `Mail`, `PirateKeys`, `Daily`; raw `GetPageAsync`/`PostAsync`/`GetApiAsync` through allowlist, limiter and guard |
| `Adventurer` | `AdventureAsync`, `FightAsync`, `FightToEndAsync(ICombatStrategy)`, `ChooseAsync`, the one runner `RunAsync(RunRequest)` |
| Strategies | `ICombatStrategy` and `IChoiceResolver` are synchronous and pure; `CombatAction.Stop` / `ChoiceDecision.Ask` hand back to a human (`RunResult.Open`) |
| `Mall` | `QuoteAsync` (plan, no purchase) and `BuyAsync` (reads again, never above `MaxUnitPrice`, keeps `MeatReserve`, checks the inventory afterwards) |
| `KolibriEvents` | `Request` (redacted path), `Encounter`, `FightEnded`, `ItemAcquired`, `StatusChanged`, `HandlerFailed` |
| `IActionGuard` | the only policy hook; grants, budgets, approval tokens and audit are in the optional package `KoLibri.Safety` |

## Safety defaults

- Every request goes through the KoL host allowlist and the limiter (actions serial, at least 1 s apart; default 2 s).
- Requests that change the account are never repeated automatically. A login page on a read triggers one
  reconnect with the stored password; on an action it ends the call with `SessionExpiredException`.
- Unclear outcomes stay unclear: `ActionStatus.Unclear`, `PurchaseStatus.Unclear`, `MailSendStatus.Unclear`.
- Combat macros exist only as an `[Experimental("KOLIBRI0001")]` type and are refused in 0.5.

## Offline tests

`KolibriOptions.HttpHandler` replaces the network transport (allowlist and limiter stay in front of it) and
`KolibriOptions.AccountStore` replaces the account files. `tests/KoLibri.Tests` uses both.
