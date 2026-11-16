# Own-shop buyer analytics

KoLibri reads the authenticated character's sales activity with
`GET backoffice.php?which=3`. This is the same read-only page used by
KoLmafia's `get_shop_log()` helper. A log row contains a timestamp, buyer
character name, quantity, item display name and total Meat, for example:

```text
12/11/17 18:40:49 Buyer bought 14 (coconut shell) for 1,400 Meat.
```

The adapter caches the page for 15 minutes, keeps a 24-hour stale snapshot on
temporary failures, and waits at least one second between requests. It never
sends a form or performs a purchase, transfer or shop mutation.

## Privacy defaults

Raw buyer names are not serialized in `ShopSale` or normal reports. The name is
normalised in memory and converted to an HMAC-SHA256 key such as
`buyer_5f...`. Direct parser use defaults to a random process key. The CLI uses
`BuyerCrmStore`, which persists the HMAC key in the encrypted local CRM vault,
so blocklists and points remain attached to the same buyer across runs. That
key and the resolved character names stay local and must not be committed.

The sales page does not provide an item id, so reports use the item display
name. A future item-catalog lookup can add ids without changing the privacy
boundary.

## Report fields and signals

`ShopBuyerAnalytics.Analyze` groups sales by the hashed buyer key and reports
sale count, distinct item count, quantity, total Meat, first/last observed
timestamp, weighted unit price, and a bounded item list. Optional flags are:

* `repeat-buyer` — multiple sale rows for the same buyer key;
* `high-value` — total Meat at or above the configured threshold;
* `bulk` — total quantity at or above the configured threshold;
* `burst-pattern-low-confidence` — several purchases in a short window;
* `configured-account-match` — the key matches an account explicitly supplied
  by the owner.

The report may include `PossibleSharedOperators`. These are weak, pattern-only
signals based on strong item overlap and nearby timestamps between two hashed
buyer keys. They are not account-identity claims, are not sent to KoL, and do
not trigger contact, blocking, repricing or purchases. Treat them as a prompt
for manual review of one's own records, never as evidence about another
player.

## Stable profiles, points and future contact

The process-local HMAC key is no longer the only identity state. The CLI CRM
command now uses `BuyerCrmStore`, which keeps a stable key and the buyer's raw
character name in an owner-only encrypted local vault under the OS application
data directory. Normal JSON reports still expose only the stable key. The
ledger stores total Meat, quantity, sale count, first/last purchase, block flag
and loyalty points. Sale fingerprints make repeated 15-minute log snapshots
idempotent.

The default points policy grants one point per 1,000 Meat plus one point per
sale; this is a policy value and can be changed later. `BuyerCrmService` also
creates `BuyerContactIntent` previews for a message or gift after a points
threshold. Intents contain the resolved character name for the eventual
adapter, but always carry `RequiresApproval`; no message or gift is sent by
the current CLI. `BuyerCrmService.SetBlocked` changes the local block flag and
optional note; blocked profiles cannot receive reward intents.

Eine lokale Blockierung kann über den stabilen Schlüssel gesetzt werden:

```bash
dotnet run --project tools/KolLibri.Cli/KolLibri.Cli.csproj -- \\
  crm-block --key buyer_... --blocked true --note "no promo"
```

