# MCP interface (read-only by default)

KoLibri can be exposed through a small MCP server without giving a model direct access to credentials or arbitrary HTTP. The server should construct the existing `IAccountReadClient` and `PricingEngine` behind a fixed tool allowlist.

## Tools

All tools return JSON objects and include `fetchedAt` and `isStale` where the underlying model exposes them. Errors are returned as MCP tool errors; do not include cookies, passwords, session tokens, or raw HTTP responses.

The default server registers only the core (status, inventory, adventure/fight/choice, item use, eat/drink,
rest, mall search and buy, KMail, daily audit/run). The full list, the rights per tool and the experimental set are
in [mcp-access.md](mcp-access.md#tool-surface-core-by-default); `McpToolSurfaceTests` pins the default list.
Arbitrage, bribes, CRM, reprice and analytics are registered only with `kolibri-mcp --experimental` or
`KOLIBRI_EXPERIMENTAL=1`; unregistered tools are absent from `tools/list` and their rights from the consent page.

| Tool | Input | Purpose | Side effects |
| --- | --- | --- | --- |
| `kol_status` | `{ "forceRefresh": false, "role": "Main" }` | Fetch the current KoL status (`name`, adventures, meat, level). | Read-only HTTPS request; cached and rate-limited. |
| `kol_inventory` | `{ "forceRefresh": false, "role": "Main" }` | Fetch inventory item IDs, names and quantities. | Read-only HTTPS request; cached and rate-limited. |
| `kol_price_summary` | `{ "itemId": "...", "forceRefresh": false, "role": "Mallbot" }` | Mall search: lowest buy/sell and weighted median prices. | Read-only provider request; cached. |
| `kol_mall_buy` | `{ "itemId": 1, "itemName": "...", "quantity": 1, "maxPricePerUnit": 100 }` | Buy under a price ceiling. | Two-step: preview with `planId`, then execute once. |
| `kol_adventure_run_execute` | `{ "locationId": "...", "execute": true }` | Start or continue a bounded adventure run (fight actions via `fightActions`). | Two-step; books `adventure`. |
| `kol_choice_pending` / `kol_choice_chain_execute` | `{ "role": "Main" }` | Show an open choice / submit an allowlisted option. | Read / two-step. |
| `kol_arbitrage_plan` (experimental) | `{ "itemId": 10881, "budget": 2000000, "marginPercent": 10 }` | Plan buying an item below its resale price from live Mall offers (read-only). | Buys nothing; run the steps with `kol_mall_buy`. |

`role` is optional on read tools and selects the configured `Main` or
`Mallbot` runtime scope. The MCP request never contains a cookie, password or
session token; the host loads encrypted credentials from the account store and
binds them to each role's read clients before serving requests.

## Approval protocol for future mutations

A mutating tool (for example, `kol_purchase`, `kol_sell`, or `kol_transfer`) must not be implemented as a direct wrapper around an HTTP route. It should use a two-step protocol:

1. `kol_preview_*` validates the request against a fresh status snapshot and `AutomationPolicy`, then returns a review object:

```json
{
  "actionId": "uuid",
  "kind": "purchase",
  "account": "Mallbot",
  "itemId": "1234",
  "quantity": 2,
  "unitPrice": 1000,
  "estimatedMeat": 2000,
  "dryRun": true,
  "requiresApproval": true,
  "expiresAt": "2026-11-16T12:00:00Z",
  "warnings": []
}
```

2. The caller presents that object to a human. A future `kol_commit_action` accepts only the opaque `actionId` plus a short-lived `approvalToken` issued by the host. The server re-fetches status/prices, revalidates all limits, and rejects stale or changed previews. `AutomationPolicy.DryRun` and the per-action approval flags remain enforced server-side; a model cannot override them through tool arguments.

The commit response should include an audit record (`actionId`, actor, account, item, quantity, price, committedAt, policy snapshot, and result) and must never include credentials. If any check fails, return a structured refusal with the failed policy names and leave the account untouched.

## Transport and host boundary

Use MCP stdio or a loopback transport. Keep the process in the same trust boundary as the encrypted account store, pass account selection by profile name, and load secrets through `FileSecretStore`/`AccountSessionManager`. Never accept a cookie, password, arbitrary URL, or route name from tool input. The host should set request and execution timeouts and redact exception messages before returning them to the model.
