# MCP interface (read-only by default)

KoLibri can be exposed through a small MCP server without giving a model direct access to credentials or arbitrary HTTP. The server should construct the existing `IAccountReadClient` and `PricingEngine` behind a fixed tool allowlist.

## Tools

All tools return JSON objects and include `fetchedAt` and `isStale` where the underlying model exposes them. Errors are returned as MCP tool errors; do not include cookies, passwords, session tokens, or raw HTTP responses.

| Tool | Input | Purpose | Side effects |
| --- | --- | --- | --- |
| `kol_status` | `{ "forceRefresh": false }` | Fetch the current KoL status (`name`, adventures, meat, level). | Read-only HTTPS request; cached and rate-limited. |
| `kol_inventory` | `{ "forceRefresh": false }` | Fetch inventory item IDs, names and quantities. | Read-only HTTPS request; cached and rate-limited. |
| `kol_price_summary` | `{ "itemId": "...", "forceRefresh": false }` | Return lowest buy/sell and weighted median prices. | Read-only provider request; cached. |
| `kol_price_recommendation` | `{ "itemId": "...", "acquisitionCost": 123.0, "forceRefresh": false }` | Explain whether an item is underpriced and provide a target sell price. | Read-only provider request; cached. |
| `kol_plan_daily_adventures` | `{ "inventory": [...] }` | Build the daily adventure plan from local state. | Pure computation; no KoL request. |
| `kol_preview_purchase` | `{ "itemId": "...", "quantity": 1, "unitPrice": 0, "approvalToken": null }` | Validate a proposed purchase against `AutomationPolicy`; produce a preview only. | Never submits an order. |
| `kol_preview_transfer` | `{ "itemId": "...", "quantity": 1, "from": "Main", "to": "Mallbot", "approvalToken": null }` | Validate a proposed transfer against policy; produce a preview only. | Never transfers an item. |

The server should advertise only the first six tools unless the caller explicitly requests mutation workflows. `kol_preview_*` tools remain non-mutating and are useful for a human approval UI.

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
