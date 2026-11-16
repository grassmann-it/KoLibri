# Adventure execution notes for LibKolibri

KoLmafia's current request layer is a useful reference for a small, safer
adventure adapter. The implementation is split by server page, while
`GenericRequest` owns the response/redirect dispatch:

- [`AdventureRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/AdventureRequest.java)
  posts `adventure.php` with `snarfblat=<location id>`, performs location
  validation, and registers the encounter.
- [`FightRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/FightRequest.java)
  initializes round zero, submits `fight.php` actions, parses each response,
  and repeats while a fight is active. It tracks multi-fights and choices that
  follow a fight.
- [`ChoiceManager`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/session/ChoiceManager.java)
  keeps `handlingChoice`, `lastChoice`, `lastDecision`, and response text while
  a choice chain is active. It extracts the current choice, validates the
  option against the page, submits `choice.php`, and loops until no choice form
  remains.
- [`GenericRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/GenericRequest.java)
  handles session checks, redirects, timeout policy, and the dispatch order:
  choice hooks, encounter registration, result processing, then choice hooks
  after results. A redirect from an adventure or choice into `fight.php` is
  handed to `FightRequest`; a choice redirect is handed back to
  `ChoiceManager`.

The test suite uses saved HTML fixtures and a fake HTTP client. See
[`AdventureRequestTest`](https://github.com/kolmafia/kolmafia/blob/main/src/test/java/net/sourceforge/kolmafia/request/AdventureRequestTest.java),
[`FightRequestTest`](https://github.com/kolmafia/kolmafia/blob/main/src/test/java/net/sourceforge/kolmafia/request/FightRequestTest.java),
and [`ChoiceManagerTest`](https://github.com/kolmafia/kolmafia/blob/main/src/test/java/net/sourceforge/kolmafia/session/ChoiceManagerTest.java).
This is the right shape for LibKolibri tests: parser tests should never call
KoL, and fixture tests should assert both the parsed state and the next request.

## LibKolibri boundary

Keep planning separate from execution. The planner can produce a daily target
such as 100 Knucklebones, a turn budget, and permitted actions. The execution
adapter should expose the following internal phases:

`Adventure -> Fight -> Choice -> Complete`, with `Blocked` and `Unknown`
terminal states for anything the policy does not understand.

An execution checkpoint should include the account role, location id, current
phase, encounter/monster, choice id and selected option, combat round,
adventures before/after, retry count, and a hash of the last response. Persist
the checkpoint after every server response. This permits a resume after a
crash without guessing whether a turn was spent.

Use a transport interface rather than exposing HTML to the planner:

```text
StartAdventure(locationId)
SubmitChoice(choiceId, option, extraFields)
SubmitFightAction(action)
RefreshEncounter()
```

The response parser classifies redirects and pages before any policy chooses an
action. A choice policy must refuse unknown choices, missing options, and
manual-control options. A combat policy must refuse unknown monsters, an
unexpected round, depleted recovery resources, or a macro/action error. The
safe result is `Blocked` with the checkpoint available for manual review.

KoLmafia retries an `AdventureRequest` on timeout and has a broader retry rule
for empty-form requests. LibKolibri should be stricter: a timeout on a
non-empty choice or fight POST is indeterminate, so refresh and compare the
current encounter/checkpoint before resubmitting. Only idempotent reads and
empty-form GETs may retry automatically. Session redirects or login pages must
stop the runner and require a fresh login.

The scripting manual's `adventure [#] <location>` and `conditions ...`
semantics are a useful model for stop conditions:
[`scripting.html`](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html).
For the Skeleton of Crimbos Past workflow, stop when the bone target is met,
the turn budget is exhausted, the daily resource cap is reached, or an
unsupported encounter is returned. Keep the initial implementation dry-run
and fixture-backed; enable live execution only behind a separate approval
gateway.
