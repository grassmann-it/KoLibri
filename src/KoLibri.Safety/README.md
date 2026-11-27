# KoLibri.Safety

Optional guards for [KoLibri](https://www.nuget.org/packages/KoLibri): grants, budgets with a usage ledger,
approval tokens and a hash-chained audit journal. Every guard is an `IActionGuard`, the one policy hook of KoLibri.

```csharp
using KoLibri;
using KoLibri.Safety;

// Combine guards; the first one that says no stops the action (ActionDeniedException).
// See the public types of this package (CompositeGuard, BudgetGuard, ...) for the available pieces.
var client = KolibriClient.Create(new KolibriOptions { ActionGuard = new CompositeGuard(/* your guards */) });
```

Without this package KoLibri runs unguarded apart from its own allowlist and rate limiter.
