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
| `account.read`, `market.read` | none |
| `adventure` | adventures per KoL day |
| `mall.buy` | meat per day, max price per unit, meat reserve |
| `daily.run` | runs per day |
| `transfer.meat`, `transfer.items` | amount per day |
| `message.send`, `item.use` | count per day |
| `shop.reprice` | changes per day, max change in percent |

Usage is counted per KoL day (rollover about 03:30 US Eastern), held while an action runs and booked only
when the action went through, in a hash-chained journal (`usage.jsonl`). A refused call returns a stable code
(`BudgetExhausted`, `RightNotGranted`, `AccountNotGranted`, `PriceAboveLimit`, `BelowMeatReserve`,
`Expired`, `Revoked`) so the AI can tell you what to approve next.

Manage grants: `kolibri-mcp grants list`, `kolibri-mcp grants revoke <id>`.

## CLI agent mode

If the CLI detects an AI harness (known environment variables, or input and output both redirected), commands
with `--execute`, `--commit` or `--sell` ask for confirmation at `/dev/tty`. Without a terminal they refuse
and send nothing. `kolibri agent-mode allow` (needs a real terminal) lets your own scripts write again.

## Honest limits

- Agent mode and token limits protect against mistakes and against an AI bending the rules. They do **not**
  stop a hostile program that runs as your operating-system user: it can read the stored game sessions
  directly. Limits only hold if the AI has **only MCP**, or runs in a sandbox without access to
  `~/.local/share/KoLibri`.
- The confirmation code and `/dev/tty` prompt stop AIs that only have a command channel or a browser tool. An
  AI that controls your terminal could also see the code.
- Adventure calls book the requested turn cap when the call ran through, not the exact turns used.
- Write tools over MCP today: `kol_mall_buy` (right `mall.buy`) and `kol_meme_shop` (right `daily.run`). Both are
  two-step: the first call previews and returns a `planId`, the second call with the same arguments executes
  once. Not exposed yet: transfer, messages, item use, shop repricing (rights exist, tools do not). BACON bought
  during a meme-shop run is limited by `daily.run`'s price ceiling but not counted against the `mall.buy` budget.
- The adventure execute tools are only registered when an execution bridge is configured, which `kolibri-mcp`
  does not do yet.
- Not yet verified against a real KoL session through MCP; the tools are covered by tests with a fake account.
