# MCP access for AI clients

## Model

- **You** (CLI, Desktop) act directly. No tokens, no budgets.
- **An AI client** (MCP) gets only what you approve in the browser, with limits.
- One enforcement point (`LibKolibri.Access.AccessGate`) checks every tool call. Tools without an
  assigned right are closed over HTTP.

## Connecting

1. `kolibri-mcp` (default `127.0.0.1:8765`, never exposed to the network).
2. Point the client at `http://127.0.0.1:8765/mcp`. It gets `401` with the metadata location, registers
   itself (RFC 7591), and opens `/authorize` in your browser (OAuth code flow with PKCE S256).
3. The page shows the requested rights as editable switches and numbers. All text comes from the fixed
   catalog; what the client says about itself is shown separately and escaped. Nothing is pre-filled to
   "everything". The consent code is printed in the terminal that runs `kolibri-mcp`; without it the
   page does not approve (`--no-code` turns this off).
4. The client receives a token valid for the lifetime you chose. Only its SHA-256 hash is stored.

A client may suggest limits with the extra authorize parameters `kolibri_limits`
(`{"mall.buy":{"dailyBudget":5000000}}`), `kolibri_accounts` and `kolibri_reason`. You can change every value.

## Rights and limits

| Right | Limits |
|---|---|
| `account.read`, `market.read`, `shop.read` | none |
| `adventure` | adventures per KoL day |
| `mall.buy` | meat per day, max price per unit, meat reserve |
| `daily.run` | runs per day |
| `transfer.meat`, `transfer.items` | amount per day |
| `message.send`, `item.use` | count per day (`item.use` also covers `kol_consume` and `kol_rest`) |
| `shop.reprice` | changes per day, max change in percent |

Usage is counted per KoL day (rollover about 03:30 US Eastern), held while an action runs and booked only
when the action went through, in a hash-chained journal (`usage.jsonl`). A refused call returns a stable code
(`BudgetExhausted`, `RightNotGranted`, `AccountNotGranted`, `PriceAboveLimit`, `BelowMeatReserve`,
`Expired`, `Revoked`) so the AI can tell you what to approve next.

Manage grants: `kolibri-mcp grants list`, `kolibri-mcp grants revoke <id>`.

## How arguments are bound to rights

The policy and the tool handlers use the same strict parser. `execute` must be a JSON boolean (a string such as
`"true"` is refused); `role` and every entry of `roles` (`"all"` = both) are authorized, so a grant for Main alone
cannot read or touch Mallbot. Read tools never execute: `kol_run_daily_bridge` refuses `execute=true`.
`kol_login_status` lists only accounts the grant covers. `kol_daily_audit` reads the fixed bridge audit store
(no `path` argument) and returns only rows whose account id equals a granted role name.
`kol_adventure_run_execute` / `kol_choice_chain_execute` with `execute=true` and no `planId` only preview and return
a single-use `planId`; the second call with the same arguments plus `planId` runs, charged against the `adventure`
budget by the largest of `maxTurns` / `maxGatewaySteps` (or `maxChoices`; steps and choices default to 20).

## Shop analytics and customer rewards

Read tools with right `shop.read`: `kol_shop_sales` (own store log per item, top buyers by Meat),
`kol_shop_buyers` (local loyalty ledger: 1 point per 1,000 Meat plus 1 per sale), `kol_shop_price_suggestions`
(own listings against the Mall floor and recent sales). Apply prices with `kol_reprice_shop`. Customers are
identified by a stable key; the character name stays in the encrypted local CRM vault. `kol_reward_buyer`
sends a KMail (optionally with an item and spending points) to a key from the ledger; blocked buyers are refused.

## Arbitrage plan

`kol_arbitrage_plan` (right `market.read`, read-only) plans buying an item below its resale price from live Mall
offers: per shop quantity and total, the daily limit of each shop, budget, meat reserve and the reason for every
skipped offer. It reads only market data and the granted account's own shop price; it does not read the account's
meat (that needs `account.status`), so pass `availableMeat` if the reserve should limit the plan.

There is no separate buy tool. An AI runs the plan step by step with `kol_mall_buy` (right `mall.buy`, with its
grant, price limit and budget): for each step of `plan.steps` call `kol_mall_buy` with `itemId`, `itemName`,
`quantity` at most `step.quantity` and `maxPricePerUnit = step.unitPrice`, show the preview, and only then confirm.
Plan again after every purchase (stock, price and daily limits move), and never repeat a purchase whose result was
reported as unclear.

## CLI agent mode

If the CLI detects an AI harness (known environment variables, or input and output both redirected), commands
with `--execute`, `--commit` or `--sell` (so also `kolibri arbitrage buy --execute`) ask for confirmation at `/dev/tty`. Without a terminal they refuse
and send nothing. `kolibri agent-mode allow` (needs a real terminal) lets your own scripts write again.

## Honest limits

- Agent mode and token limits protect against mistakes and against an AI bending the rules. They do **not**
  stop a hostile program that runs as your operating-system user: it can read the stored game sessions
  directly. Limits only hold if the AI has **only MCP**, or runs in a sandbox without access to
  `~/.local/share/KoLibri`.
- The confirmation code and `/dev/tty` prompt stop AIs that only have a command channel or a browser tool. An
  AI that controls your terminal could also see the code.
- Adventure calls book the requested turn cap when the call ran through, not the exact turns used.
- Write tools over MCP: `kol_mall_buy` (`mall.buy`), `kol_meme_shop` (`daily.run`), `kol_transfer`
  (`transfer.items` / `transfer.meat`, only to the user's other account, both accounts must be approved),
  `kol_send_message` (`message.send`, any KoL player, text shown in the preview), `kol_use_item` (`item.use`),
  `kol_consume` (`item.use`, eat/drink/spleen), `kol_rest` (`item.use`, plus the `adventure` right because a rest that is not free costs adventures; both are reserved for the worst case),
  `kol_reprice_shop` (`shop.reprice`, preview reads the store page), `kol_reward_buyer` (`message.send`, plus
  `transfer.items` with a gift; the AI only sees buyer keys, never character names), and the adventure tools
  `kol_adventure_run_execute` / `kol_choice_chain_execute` (`adventure`). All writes are two-step: the first
  call previews and returns a `planId`, the second call with the same arguments executes once.
- `cast` and `relay` exist only in the CLI; there is no MCP tool for them.
- BACON topped up during a meme-shop run needs `mall.buy` as well and counts against its daily budget.
- Adventure calls book the requested turn cap when the call ran through, not the exact turns used.
  The adventure session is created once per account when first used; if KoL ends it, restart `kolibri-mcp`.
- `kol_plan_daily_adventures_execute` is not offered over HTTP (it runs on a mock transport).
- Verified against a real KoL session on 21 Nov 2026: reading status, `kol_mall_buy` (1 BACON, 145 meat) and
  `kol_transfer` (Mallbot to Main), each booked in the usage ledger after KoL confirmed it. The other write tools
  are covered by tests with a fake account and fixture transport only.

## Open choice adventures

While a choice adventure is open KoL redirects every page to `choice.php`, so a run that stops on a choice must
never leave it unreported. `kol_choice_pending` (read only, needs `account.status` on the role) says whether a
choice or fight is open and returns the choice id, title and visible options. `kol_adventure_run_execute` and
`kol_choice_chain_execute` return `needsDecision` (run id, choice id, title, options, reason) with stop reason
`NeedsDecision` when no default answers the choice. **AI clients must show these options to the user and ask which
one to take; never guess.** The user's decision is submitted with `kolibri choice submit` (see usage). Automatic
answers come only from the user's choice policy (`kolibri choice policy`, default mode `ask`).
