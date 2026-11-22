# Using KoLibri

> **Early Alpha (0.4.0).** KoLibri is usable but not stable; commands, options and file formats may change before 1.0.
> - **Included:** `LibKolibri` (library, source checkout; NuGet planned for 0.5), `kolicli` / `kolibri` (CLI), `kolibri-mcp` (MCP server), and the desktop UI (an early example client, not broadly tested live).
> - **Unstable:** the desktop UI, MCP tool names and schemas, daily/arbitrage planners, and the Windows and macOS builds (validated manually; CI test failures there are tolerated for this release).
> - **Live use:** needs your own Kingdom of Loathing account. Requests are rate limited in two lanes (actions one per 2 s, `KOLIBRI_MIN_REQUEST_INTERVAL_MS`; display reads a little faster and side by side; see [usage](usage.md)). Use one session per account; logging in elsewhere can end the stored session. Anything that spends Meat or turns needs `--execute` in the CLI; AI agents must get your confirmation first.
> - Details: [CHANGELOG.md](../CHANGELOG.md).


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

### Buying an item below its resale price (arbitrage)

```bash
kolibri mall --role mallbot --item 10881                       # real offers by item id: shop, price, stock, daily limit
kolibri arbitrage plan --role mallbot --item 10881             # read-only plan
kolibri arbitrage plan --role mallbot --item 10881 --max-price 27000 --budget 2000000 --margin 10
kolibri arbitrage buy  --role mallbot --item 10881             # plan plus a preview of every purchase, sends nothing
kolibri arbitrage buy  --role mallbot --item 10881 --execute --approval-token TOKEN
kolibri arbitrage buy  ... --execute --approval-token TOKEN --then list --price 32995   # stock it in the own shop
kolibri arbitrage buy  ... --execute --approval-token TOKEN --then send-to main         # KMail to your other account
```

The plan takes the resale reference as the lower of your own shop price and the Pricegun median (or
`--reference-price`), and buys only up to `reference x (1 - margin)` (default margin 10 %, 0 to 90; `--tax` takes a
mall tax off the resale price, default 0). Offers are taken cheapest first. Per shop it never plans more than the
daily limit (an unreadable limit plans 1 unit; "no limit" is read from the page) and it stays inside `--budget`
(default 2,000,000, up to 1,000,000,000), keeps `--reserve` meat back (default 0) and stops at `--units` per run
(default 200). Every skipped offer says why (above the ceiling, own shop, no stock, budget, reserve, ...). The meat
a shop's daily limit already used up today cannot be read, so run `plan` again after buying.

`buy --execute` goes through the same single purchase path as the desktop Buy button (`MallPurchaseGateway`): each
step is previewed first, sends at most one purchase POST, is checked against the meat and inventory change, and its
meat is written to `arbitrage-ledger.json` (next to the KoLibri settings) before the POST. An unclear step ends the
run and is never retried. `--then` only runs after verified purchases; `send-to` only goes to your own other account.

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

Requests go through one limiter per process, shared by all accounts and roles (Main, Mallbot), every manually followed
redirect and every tool, with two lanes like a browser's main frame and sidebar:

- **Action lane**: anything that changes state or spends turns (adventure, fight, choice, eat/drink/spleen, mall buys,
  kmail, every POST, and any page not known to be a plain read). Strictly one at a time, started at least 2 s apart
  (`KOLIBRI_MIN_REQUEST_INTERVAL_MS`, never below 1000). The lane stays occupied until the response has arrived.
- **Read lane**: idempotent display reads (`api.php` status, `charpane.php`, inventory, mall search, item descriptions).
  Default: starts 1 s apart, at most 2 in flight, so a sidebar or status refresh runs next to an adventure step instead of
  queueing behind it.
- **Global cap**: at most 2 request starts per second over both lanes (a safety net, never above 5).

Presets and overrides: `KOLIBRI_LIMITER_PRESET=browser-like` (or `"limiterPreset": "browser-like"` in the settings file)
reads every 0.4 s, 3 in flight, cap 4 per second; actions keep their 2 s. Single values: `KOLIBRI_READ_INTERVAL_MS`
(min 200), `KOLIBRI_READ_CONCURRENCY` (1 to 4), `KOLIBRI_MAX_REQUESTS_PER_SECOND` (1 to 5); environment beats settings
(`kolReadIntervalSeconds`, `kolReadConcurrency`, `kolMaxPerSecond`), settings beat the preset. After an adventure step the
status and inventory refresh is one parallel read pair, not a second sequential round trip.

Separate processes (for example the CLI and the desktop app running at once) do not coordinate, so together they can
exceed these limits. Pricegun is a different host with its own per-process limiter (2 s,
`KOLIBRI_PRICEGUN_MIN_REQUEST_INTERVAL_MS`); cached results send nothing.

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

## Choice adventures

If a choice adventure appears during `adventure run` or `adventure choice-chain`, the run answers it only when the
choice policy has an answer; otherwise it stops with `stopReason: NeedsDecision` and exit code 4 (not an error). The
JSON `needsDecision` lists the choice id, title, options and the exact commands to resume. A choice left open
blocks every page of the game, so always resolve it:

```bash
kolibri choice pending --role mallbot                  # read only: id, title, options (exit 4 if one is open)
kolibri choice submit --role mallbot --choice ID --option N                       # preview
kolibri choice submit --role mallbot --choice ID --option N --execute --approval-token TOKEN
kolibri choice policy list
kolibri choice policy set --choice ID --option N       # always use option N for this choice
kolibri choice policy set --mode auto-safe             # also allow built-in/"leave it" options that cost nothing
kolibri choice policy clear --choice ID                # or --all
```

Policy file: `choice-policy.json` in the KoLibri app data directory (override with `KOLIBRI_CHOICE_POLICY`).
Limits `--max-meat` (0-1,000,000), `--max-hp-percent` (0-100), `--max-adventures` (0-10) default to 0 and only apply
to built-in options; a per-choice default is your explicit decision. The built-in table is empty until choice
data is verified (KoLmafia's bundled data has no choice table); `auto-safe` therefore only takes an option whose text
clearly means leave/ignore/skip and mentions no cost, and only if exactly one option does.
