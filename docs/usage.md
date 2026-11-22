# Using KoLibri

KoLibri has four ways in. All of them use the same stored game sessions and the same core.

| You are | Use | Command |
|---|---|---|
| a player at the keyboard | CLI | `kolibri` (alias `kolicli`) |
| a player who prefers windows | Desktop app (early alpha) | `kolibri-desktop` |
| an AI assistant (Claude, Codex, ...) | MCP server | `kolibri-mcp --http`, then add `http://127.0.0.1:8765/mcp` as a connector |
| a .NET developer | Library | `dotnet add package KoLibri.LibKolibri` |

## Everyday

```bash
kolibri login main                  # once per account; password is asked, session stored encrypted
kolibri status --role main
kolibri daily-go --role all         # preview of the day
kolibri daily-go --role all --execute
kolibri crm --role mallbot          # own shop: who bought what, loyalty points
kolibri reprice --role mallbot      # price suggestions against the mall floor
```

The CLI never changes the account without `--execute` or `--commit`.

### Relay browser (debugging)

```bash
kolibri relay --role main           # then open http://127.0.0.1:8765/main.php
```

A local proxy that lets your own browser use the stored KoLibri session, so you can click through the game and compare it with what the CLI does. Every request is logged to `~/.local/share/KoLibri/relay-log.jsonl` (headers, form field names, status, redirect target; cookies and `pwd` hashes are never logged). It listens on 127.0.0.1 only. Logging in to KoL separately in a browser can end the stored session, so browse through the relay instead and run `kolibri login refresh --role main` if KoL redirects to the login page.

## With an AI assistant

```bash
kolibri-mcp --http                  # prints a consent code in this terminal
```

The assistant is sent to a consent page, you choose which rights it gets (read, buy, adventure, message, ...)
and the limits (Meat per day, price ceiling, reserve). Everything outside that is refused. See
[mcp-access.md](mcp-access.md). Shop analytics and customer rewards use the `shop.read` right.

## Request rate

KoLibri sends at most one request to the game every 2 seconds, counted per process: all accounts and roles
(Main, Mallbot), every manually followed redirect and every tool inside the process share one limiter. Raise it
with `KOLIBRI_MIN_REQUEST_INTERVAL_MS` (never below 1000). Separate processes (for example the CLI and the
desktop app running at once) do not coordinate, so together they can exceed it. Pricegun is a different host with
its own per-process limiter (2 s, `KOLIBRI_PRICEGUN_MIN_REQUEST_INTERVAL_MS`); cached results send nothing.

## As a library

```csharp
using var kol = await KolibriClient.OpenAsync(AccountRole.Main);
var status = await kol.GetStatusAsync();
var shop = await kol.GetOwnShopAsync();
```

`KolibriClient` in package `KoLibri.LibKolibri` is the front door: login, status, inventory, mall search and
guarded buying, own shop and sales log, KMail, item use, repricing, daily Meme Shop and adventure sessions. See
`src/LibKolibri/README.md` and the runnable `src/LibKolibri/sample`.
