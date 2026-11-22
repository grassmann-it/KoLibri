# Using KoLibri

KoLibri has four ways in. All of them use the same stored game sessions and the same core.

| You are | Use | Command |
|---|---|---|
| a player at the keyboard | CLI | `kolibri` (alias `kolicli`) |
| a player who prefers windows | Desktop app (early alpha) | `kolibri-desktop` |
| an AI assistant (Claude, Codex, ...) | MCP server | `kolibri-mcp --http`, then add `http://127.0.0.1:8765/mcp` as a connector |
| a .NET developer | Library | source checkout (`dotnet add reference`); NuGet package planned for 0.5 |

## Everyday

```bash
kolibri login main                  # once per account; password is asked, session stored encrypted
kolibri status --role main
kolibri daily go --role all         # preview of the day
kolibri daily go --role all --execute
kolibri crm --role mallbot          # own shop: who bought what, loyalty points
kolibri reprice --role mallbot      # price suggestions against the mall floor
```

The CLI never changes the account without `--execute` or `--commit`.

### Relay browser (debugging)

```bash
kolibri relay --role main           # prints a URL with a secret; open exactly that URL
```

A local proxy that lets your own browser use the stored KoLibri session, so you can click through the game and compare it with what the CLI does.

- **Only your browser.** It listens on 127.0.0.1 only (default port 8766, so it does not collide with the MCP server's 8765; change it with `--port`). It accepts only the exact host `127.0.0.1:PORT`. The URL printed at start contains a random per-run secret; opening it sets a `SameSite=Strict` cookie that every later request must carry. Other pages, other programs and requests with a foreign `Origin`, `Referer` or `Sec-Fetch-Site` get `403`, so a web page cannot trigger game actions through the relay. Restarting the relay creates a new secret.
- **Minimal forwarding.** Only `Accept`, `Accept-Language`, `Referer`, `Origin`, `X-Requested-With`, conditional-GET headers and the body's `Content-Type` are sent to KoL, plus the stored session cookie and User-Agent. `Set-Cookie` from KoL is not passed to the browser.
- **Streaming and limits.** Images and other binary responses are streamed. HTML, JavaScript and CSS are rewritten (KoL links point back to the relay) in the page's own charset, up to 2 MB; larger text passes through unchanged. At most 8 requests run at once; the rest wait, then get `503`. Upstream requests that take over 60 s get `504`.
- **Rate limit.** Every request to KoL goes through the same process-wide limiter as the rest of KoLibri (default one per 2 s, see `KOLIBRI_MIN_REQUEST_INTERVAL_MS`), so pages with many images load slowly. This is intended.
- **Log.** Every request is logged to `~/.local/share/KoLibri/relay-log.jsonl` and the console: method, path, status, redirect target, byte count and outcome. Query and form values are shown as `…` except a few harmless keys (`snarfblat`, `action`, `whichchoice`, `option`, `which`, `whichitem`, `ajax`); cookies, `pwd` hashes, other header values and error details are never logged or returned in error pages.

Logging in to KoL separately in a browser can end the stored session, so browse through the relay instead and run `kolibri login refresh --role main` if KoL redirects to the login page.

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

Both intervals, the cache duration, budgets and combat limits are also editable in the desktop **Settings** view
(saved as `desktop-settings.json` in the KoLibri data folder, no secrets; every value has a default, minimum and
maximum). An environment variable, when set, overrides the saved interval and the view says so. Intervals are
applied at startup, so restart after changing them. The game itself ends a fight at 50 rounds; that ceiling is fixed.

## As a library

```csharp
using var kol = await KolibriClient.OpenAsync(AccountRole.Main);
var status = await kol.GetStatusAsync();
var shop = await kol.GetOwnShopAsync();
```

`KolibriClient` in package `KoLibri.LibKolibri` is the front door: login, status, inventory, mall search and
guarded buying, own shop and sales log, KMail, item use, repricing, daily Meme Shop and adventure sessions. See
`src/LibKolibri/README.md` and the runnable `src/LibKolibri/sample`.
