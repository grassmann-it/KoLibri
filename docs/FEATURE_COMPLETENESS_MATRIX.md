# KoLmafia ↔ KoLibri: Feature Completeness Matrix

This matrix compares the current KoLmafia state (upstream `main`) with the
state in the repository. The focus is on adventure, combat and repeatable
workflows. Price/Mall functions are only mentioned where they block or secure
the adventure run.

**Status symbols:** `✓` is usable in the current scope, `◐` is a safety
core/fixture/preview without a complete live adapter, `✗` is missing, `↗` is
deliberately deferred. A positive KoLibri rating does not mean that an HTTP
mutation is already executed.

## Comparison

| Capability | KoLmafia (reference) | KoLibri today | Gap/risk | Prio | Acceptance criterion for KoLibri |
|---|---|---|---|:---:|---|
| Adventure goal planning | `adventure [#] <location>` and state/quest data control location and count. | `DailyAdventurePlanner` produces a deterministic plan for Knucklebones, Skeleton of Crimbo's Past, Lucky Surprise Eggs and turn budget; `AdventureState.IsStale` explicitly yields `BlockedStaleSnapshot`. `FarmVsBuyPlanner` compares farming with a Mall purchase. `✓` (preview) | Only a few domain-specific goals; no generic condition language. | **P1** | The plan stays blocked on missing yield, turn budget or stale snapshot and is identical across CLI/MCP/Desktop. |
| Adventure request/live transport | `AdventureRequest` sends `adventure.php` (`snarfblat`), validates locations and handles many special pages. | `AdventureExecutionGateway` persists the numeric catalog value in the checkpoint and binds it into the transport fields; CLI `adventure run --snarfblat N` can pass it on explicitly; in production only the safe transport boundary exists, the fixture implementation does not send to KoL. `◐` | No productive, route-bound adapter; `adventure-plan` stays read-only. | **P2** | For an allowlisted zone a real adapter exists with request limit, session gate, fresh status and post-verify; unsupported pages stop. |
| Redirect/phase dispatch | `GenericRequest` dispatches Adventure → Fight/Choice and handles hooks, results and redirects. | Kernel phases `Ready → Encounter → Fight/Choice → Reward/Complete/Aborted`; the gateway makes one request per step. `◐` | Redirects and follow-up fights are only covered through abstracted observations; no complete KoL dispatcher. | **P2** | Redirects are safely classified by route/parser; no implicit POST on an unknown target. |
| Encounter parsing | KoLmafia maintains extensive parsers and result registration for current pages. | `KoLAdventureEncounterParser` recognizes login, fight, choice, reward and `Unknown`, extracts round/choice/options/hash. `✓` for the minimal model, `◐` for real-world variety | A regex minimal parser cannot safely map new forms, results or special pages. | **P0** | Fixture catalog for every supported zone; an unknown or incomplete page becomes `Unknown`/`Blocked`, never guessed. |
| Unknown state / manual stop | KoLmafia can halt on errors/`abort`, but has broad fallback logic. | Unknown fight/choice/encounter, session boundary and invalid observation lead to an explicit abort with checkpoint. `✓` | Stop reasons must stay visible and resumable on all surfaces. | **P0** | Every stop contains a reason code, last response hash, checkpoint and a manual next step. |
| Choice chain | `ChoiceManager` validates `whichchoice`/`option`, passes extra fields and loops until no choice form remains. | Allowlist for choice ID/option/encounter; `BoundedChoiceChainRunner` only continues pending choice actions up to a step/time limit and stops before fight, unknown or refresh; extra fields are adopted in a bounded way. `◐` (fixture) | No general choice database, no settings-based automation, no productive transport. | **P1** for supported daily zones, **P2** broadly | Every supported choice has a parser fixture, allowed options, extra-field schema and follow-up response tests. |
| Combat request/turn loop | `FightRequest` performs round-0 initialization, `fight.php` actions, multi-fights and follow-up choices; the current upstream code is an extensive, zone-specific parser/dispatcher. | `BoundedManualCombatRunner` executes a bounded round sequence via `ManualCombatGateway`, re-inspects before every preview and stops on terminal, gateway, timeout or step-limit state. `◐` | A complete zone-specific KoL dispatcher and AutoCombat live policy are still missing. | **P2** | A bounded runner executes an allowlisted sequence, checks round/monster progress and stops on deviation. |
| Combat rules / skills / items / macros | KoLmafia uses combat preferences, combat actions, skills/items, macros, pickpocket/run and special-monster rules. | `AutoCombatPolicy` stays declarative; `AutoCombatAdventureAdapter` only translates a decision into an `AdventureFightAction` if the action is visible and enabled in the current fight snapshot. CLI/MCP can additionally apply a recovery plan as a gate. `◐` | No complete resource/recovery commit yet and no free macro system. | **P2** (bounded subset) | Declarative rules for a few known monsters/rounds (incl. dry run) are versioned separately; no free script may produce requests. |
| Recovery / resources / safety checks in combat | RecoveryManager, HP/MP thresholds, consumables, effects, equipment and familiar feed into the decision. | `RecoveryPlanner` produces a bounded read-only plan; `AccountRecoverySnapshotProvider` maps fresh status/inventory reads including HP/MP, effects, familiar and equipment; `AutoCombatAdventureAdapter` and `BoundedManualCombatRunner` optionally accept only a plan with `CanEnterCombat`, otherwise the action stays blocked before preview/POST. The real fight-gateway POST still needs a fresh, transport-bound recovery read. `◐` | Live combat without a resource gate would not be safe. | **P1** | Before every fight POST, check fresh status, recovery reserve and allowed consumables; on missing information `Blocked`. |
| Combat/choice post approval | No uniform human-approval token for every combat POST. | `IAdventureApprovalHook` before every mutating adventure/fight/choice request; `IAdventureVerificationHook` afterwards. `✓` | The hook must be connected to a real approval/audit inbox before live execution is enabled. | **P0** | Without approval no POST; a verify failure persists an abort and does not send again. |
| Timeout / retry / idempotency | Adventure/fight requests allow upstream retry-on-timeout; the broad state logic tries to keep going. | A non-idempotent POST stays `Indeterminate` on timeout; the gateway marks in-flight, requires a refresh and does not blindly resend. Request ID, fingerprint, response hash and CAS checkpoint exist. `✓` (safety core) | Reconciliation must be bound to the real KoL transport. | **P0** | A timeout fixture proves: exactly one POST, then refresh and a decision based on the current encounter; no double turn. |
| Checkpoint / crash resume | KoLmafia keeps session/preference state, but no comparable bounded CAS checkpoint per mutation. | `AdventureCheckpoint`, response history, in-flight marker, revision/CAS, `ResumeAsync` and a local JSON CAS store are implemented. `✓` (core) | Retention/cleanup policy and complete UI resume controls are still missing. | **P0** | A process abort after any server response is resumable; `Resume` first emits only a refresh, never the old POST. |
| Daily limits / counters | Conditions and session state can stop adventures; daily special rules are spread across domain logic/preferences. | `AdventureDailyLimitContract`, counter store and run reservation protect turns/runs; the planner knows goal and egg cap. `✓` for the daily plan | No general daily resource/semi-rare/choice condition. | **P1** | The limit is reserved before start, stored after the response and verified against a fresh account snapshot. |
| Conditions / objectives | `conditions add` supports items, choice adventures, level, stats, HP and MP; `adventure` halts when conditions are met. | `AdventureCondition`/`AdventureConditionSnapshot` evaluate item/count, stat, HP/MP, choice count and turns; `AccountAdventureConditionSnapshotProvider` binds fresh status/inventory reads; `BoundedAdventureRunner` stops before a POST when the goal is met or a snapshot is missing. `◐` | HP/MP/choice still need specialized combat/choice reads; a complete live-run command is missing. | **P1** | A typed `Condition` model with item/count/stat/HP/MP/choiceadv/turns; every condition yields progress and a stop reason. |
| Farm-vs-Buy / ticket economy | `acquire` can create, buy, fetch from storage or use the Mall depending on state; conditions drive adventure. | `FarmVsBuyPlanner` is a pure ROI/confidence preview; Coinmaster/one-day-ticket ranking is read/preview. `✓` (plan) | No commit adapter, no complete acquire chain. | **P1** | The farm/buy decision binds ticket/transfer/consumable costs, confidence and current Mall reference; commit stays a separate approval step. |
| Repeatable daily workflow | CLI/ASH scripts, `repeat`, moods/triggers, `breakfast` and quest commands allow extensive automation. | `KoLWorkflowPlanner` models Main/Mallbot phases with ReadOnly/Ready/ApprovalRequired/Blocked/Deferred; CLI/MCP produce a JSON plan. `adventure-plan --periodic` reads fresh on every tick; `DailyAdventurePlanExecutor` executes an already approved plan in a bounded, sequential way via a gateway callback. `✓` (plan/executor), `◐` (live adapter) | No time-based commit worker; the callback must still use an approval-bound gateway. | **P1** | A daily run is deterministic, stoppable and resumable; plan, warnings and approval ID are identical in CLI/MCP/Desktop. |
| Scheduler / watchlist | KoLmafia can repeat scripts; external scripts/cron handle scheduling. | `WatchlistScanner`/JSON store scans Mall/Pricegun read-only with a worker limit; `DailyAdventurePlanScheduler` reads a fresh snapshot per tick and produces bounded plans without mutation. No automatic purchase. `◐` | A "scheduler" must not give the impression of mutating live. | **P1** | The scheduler only produces plan/preview and audit; execution requires explicit run approval and a bounded worker. |
| CLI / MCP / UI | KoLmafia CLI, GUI, Relay Browser and ASH share session state. | CLI `adventure-plan`/`adventure run`, MCP preview tools (including `kol_adventure_daily_schedule`), the opt-in `LibKolibri` combat gateway and desktop services share planner/gateway contracts; MCP AutoCombat can additionally check a recovery snapshot as a safety gate; live CLI is explicitly `--execute` + approval token. `◐` | MCP/desktop live command and golden JSON for the complete runner are still missing. | **P1** | Golden JSON for the same snapshot in CLI/MCP/Desktop; the executor only becomes available after the same gateway contract. |
| Session/login boundary | `GenericRequest` detects login/redirect and manages cookies/session. | Encrypted profiles, login, cache and `KoLResponseClassifier` detect login/stale pages; the gateway stops at the session boundary. `✓` | The adventure transport must use the same session clients, not its own cookies. | **P0** | A login redirect or expired session produces no follow-up POST and stays visible in the audit. |
| Audit / mutation ledger | KoLmafia logs requests/events, but without the KoLibri Preview→Approval→Commit contract. | Audit journal, operation ledger and approval contracts; `AuditedAdventureHooks` writes adventure preview, approval/commit and verify with request ID, checkpoint and response hash. `✓` (contract) | The live host must inject a journal; retention/rotation is still open. | **P0** | Every POST has a plan/approval/request ID, before/after status, response hash and verify result. |
| Test/fixture strategy | Upstream contains adventure, fight and choice fixtures as well as fake-HTTP tests. | `tests/.../Fixtures/Adventure` plus parser, kernel and gateway tests; the fixture transport checks correlation/timeout/approval. `✓` for safety cases | No broad zone coverage yet and no live contract test against a route adapter. | **P0** | Every new zone delivers HTML fixtures for start/fight/choice/reward/login/unknown and tests the next request action. |
| Zone/quest/path coverage | Many special locations, quests, paths, ascension and mini-games are integrated into domain classes. | No quest/path/ascension automation goal; the generic `AdventureId` contract suffices for bounded adapters. `↗` | Full parity would be its own product and increases the safety surface. | **P2** for selected farm zones, otherwise deliberately later | Explicitly catalog supported zones; everything else stays `NotAllowlisted`/manual. |
| Equipment / familiar / modifier / maximizer | KoLmafia can choose outfit, familiar, modifiers and `maximize` for combat. | Read-only status snapshots now deliver optional familiar/equipment data to `RecoverySnapshot`; selection and modifier computation are deliberately kept out of the adventure gateway. `◐` | No deterministic selection or modifier policy. | **P2** | A read-only modifier/equipment snapshot first; selection only after a deterministic policy and preview. |
| Buffs / mood / breakfast / healing outside combat | CLI moods, triggers, breakfast, rest/relax and buff/effect management are integrated. | Not present; eggs are an explicit planner step, not a general effect policy. `✗` | Manual preparation may be required before farming. | **P2** (or deliberately P3) | Only typed, bounded preflight steps with their own approval; no ASH clone. |
| Free scripting language / Relay Browser | ASH/CLI with variables, functions, branching, loops and Relay Browser. | Deliberately no JavaScript/ASH clone; typed workflows and MCP/CLI schemas. `↗` | No drop-in compatibility for existing KoLmafia scripts. | **P3 / non-goal** | Do not plan in P0–P2; plugin/provider interface only after stable contracts. |

## Prioritized implementation

### P0 – before any live mutation

1. Fix the existing parser/kernel/gateway safety core as a binding contract:
   unknown = stop, no implicit action.
2. Complete adventure fixtures and contract tests for login redirect, stale
   session, round/choice mismatch, duplicate response, timeout, approval deny
   and checkpoint CAS.
3. Bind adventure POSTs to the existing session/request gate and audit ledger.
   No separate cookie or retry stack in the adapter.
4. Connect approval and verify hooks to the same Preview → Approval → Commit →
   Verify chain as purchase, transfer and reprice.

**P0 done:** A deliberately faulty or unknown server response reproducibly
leads to `Blocked`/`Aborted`, leaves a resumable checkpoint and sends no second
POST.

### P1 – daily, controlled added value

1. Typed conditions (item/count, turns, HP/MP, choice count, stats) and a fresh
   status/inventory read before every planned run.
2. Build a small daily runner for a cataloged zone: goal, turn budget,
   egg/ticket reserve, stop reasons and audit; the bounded core runner exists,
   CLI/MCP/Desktop preview and live status binding are missing.
3. Integrate farm-vs-buy as well as ticket/Coinmaster costs into the same
   daily plan; buying/farming stays a separate approval action.
4. Bring CLI, MCP and Desktop onto golden JSON and the same checkpoints/approval
   IDs. The existing Mall/Pricegun watchlist worker stays read-only.

**P1 done:** A user can plan a daily run, approve a bounded run, stop safely
on stale/unknown/limit and then continue without a double turn.

### P2 – targeted coverage instead of a KoLmafia clone

1. Add productive adventure/fight/choice transport for a few cataloged farm
   zones; own form fields and fixtures per zone.
2. Declarative combat rules for known monsters/rounds (attack/skill/item/run)
   as well as minimal recovery gates; no free macro/ASH execution.
3. Extend redirect/multi-fight/special-choice chains only after fixture
   evidence. Quest/path/ascension pages stay non-automatic.
4. Connect read-only equipment/familiar/modifier data when it actually
   improves farm ROI or survival decisions.

## Delimitation from KoLmafia parity

KoLmafia remains the reference for general adventure and combat breadth.
For P0/P1/P2, KoLibri aims for a smaller, verifiable subset: a few known
zones, explicit actions, fresh reads, approval and resume. ASH/Relay
compatibility, complete quest/path automation, a global choice database and
free combat macros are not a prerequisite for Mallbot/daily operation and
remain downstream.

## Sources

- Repo plan and deliberate scope: [`FEATURE_COMPLETENESS_PLAN.md`](FEATURE_COMPLETENESS_PLAN.md)
- Repo adventure reference: [`KOLMAFIA_ADVENTURE_RESEARCH.md`](KOLMAFIA_ADVENTURE_RESEARCH.md)
- KoLibri minimal parser: [`KoLAdventureEncounterParser.cs`](../src/KoLibri.Core/Adventure/KoLAdventureEncounterParser.cs)
- KoLibri state machine: [`AdventureRunKernel.cs`](../src/KoLibri.Core/Adventure/AdventureRunKernel.cs)
- KoLibri gateway/approval/CAS: [`AdventureExecutionGateway.cs`](../src/KoLibri.Core/Adventure/AdventureExecutionGateway.cs)
- KoLibri daily plan: [`DailyAdventurePlanner.cs`](../src/KoLibri.Core/Adventure/DailyAdventurePlanner.cs)
- KoLibri workflow: [`WorkflowModels.cs`](../src/KoLibri.Core/Integration/WorkflowModels.cs)
- KoLmafia [`AdventureRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/AdventureRequest.java), [`FightRequest`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/request/FightRequest.java), [`ChoiceManager`](https://github.com/kolmafia/kolmafia/blob/main/src/main/java/net/sourceforge/kolmafia/session/ChoiceManager.java)
- KoLmafia [CLI/conditions/adventure handbook](https://github.com/kolmafia/kolmafia.sourceforge.net/blob/main/scripting.html)
