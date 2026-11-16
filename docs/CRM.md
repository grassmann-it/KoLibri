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

Raw buyer names are not returned in `ShopSale`. The name is normalised in
memory and converted to an HMAC-SHA256 key such as `buyer_5f...`. A random
process key is used by default, so identifiers cannot be correlated across
separate runs. A caller can provide a locally stored secret to keep identifiers
stable between runs; that secret must remain local and must not be committed.

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

