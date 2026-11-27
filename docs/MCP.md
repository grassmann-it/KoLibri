# MCP interface

`kolibri-mcp` lets an AI client use a Kingdom of Loathing account through a fixed tool allowlist, without ever seeing
credentials or being able to send arbitrary HTTP. It is built on the `KoLibri` library: every tool call opens a fresh
session from the stored account (the same account store as the CLI), so a login, logout or renewal done in the CLI is
used at once. Access control lives in `KoLibri.Safety`; see [mcp-access.md](mcp-access.md) for rights, grants, limits
and the consent flow.

Two ways to run it:

- `kolibri-mcp` (HTTP, `127.0.0.1:8765/mcp`): OAuth consent in your browser, grants with daily limits. Read and write tools.
- `kolibri-mcp --stdio`: **read-only**. Tools that would change the account are refused over stdio.

## Tools

All tools return JSON objects. Errors are returned as MCP tool errors; they never include cookies, passwords, session
tokens or raw HTTP responses. Reads fail with a tool error when the game cannot be reached (there is no stale copy).

The default server registers 19 core tools for playing by hand (status, inventory, adventure/fight/choice, item use,
eat/drink, rest, mall search and buy, KMail, daily audit/run). The full list with the right each tool needs is in
[mcp-access.md](mcp-access.md#tool-surface-core-by-default); `McpToolSurfaceTests` pins it. Arbitrage, CRM, reprice,
transfers and the planners are registered only with `kolibri-mcp --experimental` or `KOLIBRI_EXPERIMENTAL=1`;
unregistered tools are absent from `tools/list`, cannot be called, and their rights are not offered on the consent page.

| Tool | Main input | Purpose | Side effects |
| --- | --- | --- | --- |
| `kol_status`, `kol_inventory` | `role`, `forceRefresh` | Status (name, adventures, meat, level, organs) and inventory. | Read-only, rate-limited. |
| `kol_login_status` | | Stored accounts the grant covers. | Read-only. |
| `kol_price_summary` | `itemId`, `forceRefresh`, `role` | Mall search: lowest sell price and weighted median. | Read-only. |
| `kol_mall_buy` | `itemId`, `itemName`, `quantity`, `maxPricePerUnit`, `planId` | Buy under a price ceiling. | Two-step: preview with `planId`, then execute once. Books `mall.buy`. |
| `kol_adventure_locations`, `kol_combat_inspect`, `kol_combat_decision`, `kol_choice_pending`, `kol_choice_options` | `role`, `locationId` | Look at locations, the open fight or choice and its legal options. | Read-only. |
| `kol_adventure_run_execute` | `locationId`, `execute`, `planId`, `maxTurns`, `maxGatewaySteps`, `maxDurationSeconds`, `fightActions`, `choiceActions`, `conditions`, `snarfblat`, `role` | Run a bounded adventure (one location, fights by `fightActions`, choices by `choiceActions`). | Two-step. Books `adventure` by the largest of `maxTurns` / `maxGatewaySteps`. |
| `kol_choice_chain_execute` | `runId`, `choiceActions`, `maxChoices`, `execute`, `planId`, `role` | Answer the open choice(s) with an allowlisted option. | Two-step. Books `adventure`. |
| `kol_use_item`, `kol_consume`, `kol_rest` | item / kind / times, `execute`, `planId` | Use an item; eat, drink or chew; rest. | Two-step. Book `item.use` (`kol_rest` also `adventure`). |
| `kol_send_message` | recipient, text, `planId` | KMail to any KoL player; the text is shown in the preview. | Two-step. Books `message.send`. |
| `kol_daily_audit`, `kol_run_daily_bridge` | `role` | Read the daily audit log; preview the daily bridge. | Read-only (`kol_run_daily_bridge` refuses `execute=true`). |
| `kol_meme_shop` | `planId` | Meme-shop step of the daily routine (BACON top-up needs `mall.buy`). | Two-step. Books `daily.run`. |

`role` is optional and selects the stored `Main` or `Mallbot` account; a grant for one account does not cover the other.
The request never contains a cookie, password or session token.

## Two-step writes

Every tool that changes the account follows the same flow, enforced server-side by the access gate:

1. The AI calls the tool with the arguments it wants and `execute=true` (or the tool's own confirm flag). With a grant
   that allows it, the server checks the right, the account, the limits and the daily budget, sends nothing, and returns
   a preview plus a single-use `planId`.
2. The AI shows the preview to you. If you agree it calls again with **the same arguments plus `planId`**. The server
   re-checks everything against the live state and runs the action once.
3. The usage (adventures, meat, count) is held while the action runs and booked in the usage ledger only when KoL
   confirmed it. An unclear result keeps the budget held; it is never repeated automatically.

A changed argument, an expired or used `planId`, a missing right, an exhausted budget or a revoked grant ends in a
structured refusal with a stable code (`PlanInvalid`, `RightNotGranted`, `BudgetExhausted`, `PriceAboveLimit`,
`BelowMeatReserve`, `Expired`, `Revoked`, ...) and leaves the account untouched. Over stdio, writes are refused.
`approvalToken` is only needed for a host without an access gate. Details and the full list of rights and limits:
[mcp-access.md](mcp-access.md).

## Transport and host boundary

HTTP binds to loopback only. Keep the process in the same trust boundary as the encrypted account store. Never accept a
cookie, password, arbitrary URL or route name from tool input. Exception messages are redacted before they reach the model.
